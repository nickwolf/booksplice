using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BookSplice.Core.Analysis;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Ordering;
using BookSplice.Core.Planning;
using BookSplice.Core.Presets;
using BookSplice.Core.Queue;
using BookSplice.Core.Settings;
using BookSplice.Core.Covers;
using BookSplice.Gui.Controls;
using BookSplice.Gui.Updates;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class BookHeaderTests
{
  private static readonly string[] NewButtons = ["ApplyPresetButton", "SavePresetButton", "BrowseDestinationButton"];
  private const string Reasons = "stream-copy.codec.unsupported";

  [Fact]
  public void TheHeaderShowsTheFileNameAndWhetherAudioIsCopiedOrReencoded() => DispatcherTestHost.Run(async _ =>
  {
    using var folder = new TempFolder();
    var copy = new FakePlanner(AudioStrategy.AacStreamCopy);
    using var model = Model(copy, folder.Path);
    var item = await AddAsync(model, "Made Up Tale", ready: false);
    model.SelectedItem = item;
    await model.HeaderSettledAsync();
    Assert.Equal("Choose a track order to plan the output.", item.HeaderRefusalText);
    Assert.False(item.HasHeaderPlan);

    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);
    item.Metadata.Single(field => field.Field == SemanticField.BookTitle).Value = "Made Up Tale";
    await model.HeaderSettledAsync();

    Assert.True(item.HasHeaderPlan);
    Assert.False(item.HasHeaderRefusal);
    Assert.Equal("Made Up Tale.m4b", item.HeaderFileName);
    Assert.Equal(Path.Combine(folder.Path, "Made Up Tale.m4b"), item.HeaderFullPath);
    Assert.Equal("Audio is copied without re-encoding.", item.HeaderAudioText);

    copy.Strategy = AudioStrategy.DirectTranscode;
    item.QualityProfileId = "efficient";
    await model.HeaderSettledAsync();
    Assert.Equal("Audio will be re-encoded to AAC.", item.HeaderAudioText);
    Assert.Contains("Efficient 48 kbps", item.HeaderInfoText);
  });

  [Fact]
  public void TheInfoTextCarriesReasonsStorageAndNotes() => DispatcherTestHost.Run(async _ =>
  {
    using var folder = new TempFolder();
    var space = new SpaceEstimate(400_000_000, 400_000_000, 880_000_000, 50_000_000_000, 1, "D:\\", "C:\\", 440_000_000, 440_000_000, null);
    var notes = new AnalysisDiagnostic[] { new("output.folder-skipped", AnalysisDiagnosticSeverity.Information, "The folder \"{series}\" was skipped because series is not available.") };
    var planner = new FakePlanner(AudioStrategy.FilterConcatTranscode, reasons: [Reasons, "stream-copy.sample-rate.missing"], space: space, notes: notes);
    using var model = Model(planner, folder.Path);
    var item = await AddAsync(model, "Made Up Tale");
    model.SelectedItem = item;
    await model.HeaderSettledAsync();

    var info = item.HeaderInfoText;
    Assert.True(item.HasHeaderInfo);
    Assert.Contains(Path.Combine(folder.Path, "Made Up Tale.m4b"), info);
    Assert.Contains("Why re-encode:", info);
    Assert.Contains("  - The source audio is not AAC.", info);
    Assert.Contains("Estimated output size: 381.5 MB on D: (free space: 46.6 GB)", info);
    Assert.Contains("Estimated temporary space: 381.5 MB on C: (free space unavailable)", info);
    Assert.Contains("Note: The folder \"{series}\" was skipped because series is not available.", info);
  });

  [Fact]
  public void ARefusedPlanShowsAShortVisibleReason() => DispatcherTestHost.Run(async _ =>
  {
    using var folder = new TempFolder();
    var space = new SpaceEstimate(400_000_000, 400_000_000, 880_000_000, 1_000, null, "D:\\");
    using var model = Model(new FakePlanner(refuse: true, space: space), folder.Path);
    var item = await AddAsync(model, "Made Up Tale");
    model.SelectedItem = item;
    await model.HeaderSettledAsync();

    Assert.True(item.HasHeaderRefusal);
    Assert.False(item.HasHeaderPlan);
    Assert.Equal("The destination does not have enough available space.", item.HeaderRefusalText);
    Assert.True(item.HasHeaderInfo);
    Assert.Contains("Estimated space needed: 839.2 MB on D:", item.HeaderInfoText);
  });

  [Fact]
  public void ChangesInsideTheDebounceWindowPlanOnce() => DispatcherTestHost.Run(async _ =>
  {
    using var folder = new TempFolder();
    var planner = new FakePlanner();
    using var model = Model(planner, folder.Path);
    var item = await AddAsync(model, "Made Up Tale");
    model.HeaderDebounce = TimeSpan.FromMilliseconds(150);
    model.SelectedItem = item;
    await model.HeaderSettledAsync();
    var before = planner.Calls;

    item.OutputDirectory = Directory.CreateDirectory(Path.Combine(folder.Path, "one")).FullName;
    item.OutputDirectory = Directory.CreateDirectory(Path.Combine(folder.Path, "two")).FullName;
    item.OutputDirectory = Directory.CreateDirectory(Path.Combine(folder.Path, "three")).FullName;
    await model.HeaderSettledAsync();

    Assert.Equal(before + 1, planner.Calls);
    Assert.Equal(Path.Combine(folder.Path, "three", "Made Up Tale.m4b"), item.HeaderFullPath);
  });

  [Fact]
  public void AResultThatArrivesAfterTheSelectionChangedIsDropped() => DispatcherTestHost.Run(async _ =>
  {
    using var folder = new TempFolder();
    var planner = new FakePlanner();
    using var model = Model(planner, folder.Path);
    var first = await AddAsync(model, "First Tale");
    var second = await AddAsync(model, "Second Tale");
    var hold = planner.HoldNext();

    model.SelectedItem = first;
    var slow = model.HeaderTask;
    await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    model.SelectedItem = second;
    await model.HeaderSettledAsync();
    Assert.Equal("Second Tale.m4b", second.HeaderFileName);

    hold.Release.SetResult();
    await slow;
    Assert.Equal("", first.HeaderFileName);
    Assert.Equal("Second Tale.m4b", second.HeaderFileName);
  });

  [Fact]
  public void AnOlderPlanForTheSameBookNeverOverwritesANewerOne() => DispatcherTestHost.Run(async _ =>
  {
    using var folder = new TempFolder();
    var planner = new FakePlanner();
    using var model = Model(planner, folder.Path);
    var item = await AddAsync(model, "Made Up Tale");
    var hold = planner.HoldNext();
    model.SelectedItem = item;
    var slow = model.HeaderTask;
    await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

    item.OutputDirectory = Directory.CreateDirectory(Path.Combine(folder.Path, "newer")).FullName;
    await model.HeaderSettledAsync();
    hold.Release.SetResult();
    await slow;

    Assert.Equal(Path.Combine(folder.Path, "newer", "Made Up Tale.m4b"), item.HeaderFullPath);
  });

  [Fact]
  public void AnUpdateBeingPreparedLeavesTheHeaderAloneAndItIsPlannedAgainAfterwards() => DispatcherTestHost.Run(async dispatcher =>
  {
    using var folder = new TempFolder();
    var gate = new GuiOperationGate(dispatcher);
    var planner = new FakePlanner();
    using var model = Model(planner, folder.Path, gate: gate);
    var item = await AddAsync(model, "Made Up Tale");
    model.SelectedItem = item;
    await model.HeaderSettledAsync();
    var shown = item.Header;
    var calls = planner.Calls;

    var freeze = gate.TryFreeze(0);
    Assert.NotNull(freeze);
    item.OutputDirectory = Directory.CreateDirectory(Path.Combine(folder.Path, "later")).FullName;
    await Task.Delay(100);
    Assert.Same(shown, item.Header);
    Assert.Equal(calls, planner.Calls);
    Assert.Equal("", model.Message);

    freeze.Dispose();
    await model.HeaderSettledAsync();
    Assert.Equal(Path.Combine(folder.Path, "later", "Made Up Tale.m4b"), item.HeaderFullPath);
    Assert.Equal("", model.Message);
  });

  [Fact]
  public void RemovingAnEarlierBookWithTheSameNameDropsTheNumberFromTheHeader() => DispatcherTestHost.Run(async _ =>
  {
    using var folder = new TempFolder();
    using var model = Model(new FakePlanner(), folder.Path);
    var one = await AddAsync(model, "Made Up Tale");
    var two = await AddAsync(model, "Made Up Tale");
    model.SelectedItem = two;
    await model.HeaderSettledAsync();
    Assert.Equal("Made Up Tale (2).m4b", two.HeaderFileName);

    Assert.True(model.Remove(one));
    await model.HeaderSettledAsync();

    Assert.Equal("Made Up Tale.m4b", two.HeaderFileName);
  });

  [Fact]
  public void AnEarlierBookFailingFreesItsNameInTheHeader() => DispatcherTestHost.Run(async _ =>
  {
    using var folder = new TempFolder();
    using var model = Model(new FakePlanner(), folder.Path, new Converter(ConversionTerminalStatus.ExecutionFailed));
    var one = await AddAsync(model, "Made Up Tale");
    var two = await AddAsync(model, "Made Up Tale");
    model.SelectedItem = two;
    await model.HeaderSettledAsync();
    Assert.Equal("Made Up Tale (2).m4b", two.HeaderFileName);

    await model.ConvertAsync(one);
    await model.HeaderSettledAsync();

    Assert.Equal("Made Up Tale.m4b", two.HeaderFileName);
  });

  [Fact]
  public void OnlyTheSelectedBookIsPlanned() => DispatcherTestHost.Run(async _ =>
  {
    using var folder = new TempFolder();
    var planner = new FakePlanner();
    using var model = Model(planner, folder.Path);
    var one = await AddAsync(model, "First Tale");
    var two = await AddAsync(model, "Second Tale");
    model.SelectedItem = two;
    await model.HeaderSettledAsync();
    one.OutputDirectory = Directory.CreateDirectory(Path.Combine(folder.Path, "elsewhere")).FullName;
    await model.HeaderSettledAsync();

    Assert.Null(one.Header);
    Assert.Equal("Second Tale.m4b", two.HeaderFileName);
  });

  [Fact]
  public void ABookWithAMissingProfileSaysSoInTheHeader() => DispatcherTestHost.Run(async _ =>
  {
    using var folder = new TempFolder();
    using var model = Model(new FakePlanner(), folder.Path);
    model.Settings = model.Settings with { QualityProfileId = "gone-quality" };
    var item = await AddAsync(model, "Made Up Tale");
    model.SelectedItem = item;
    await model.HeaderSettledAsync();

    Assert.True(item.HasHeaderRefusal);
    Assert.Contains("gone-quality", item.HeaderRefusalText);
    Assert.Contains("Choose an audio quality", item.HeaderRefusalText);
  });

  [Fact]
  public void TheWindowShowsTheHeaderAndHasNoPreviewButtonOrOutputTab() => RunWindow(async (window, model, root) =>
  {
    var item = await AddAsync(model, "Made Up Tale");
    var other = await AddAsync(model, "Another Tale");
    var queue = (ListBox)window.FindName("Queue");
    queue.SelectedItem = item;
    Assert.Same(item, model.SelectedItem);
    await model.HeaderSettledAsync();
    Settle(window);

    var output = (TextBlock)window.FindName("HeaderOutput");
    Assert.Equal(Visibility.Visible, output.Visibility);
    Assert.Contains("Made Up Tale.m4b", string.Concat(output.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text)));
    Assert.Equal(item.HeaderFullPath, output.ToolTip);
    Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("HeaderAudio")).Visibility);
    Assert.Equal(Visibility.Visible, ((InfoIcon)window.FindName("HeaderInfo")).Visibility);
    Assert.Equal(item.HeaderInfoText, ((InfoIcon)window.FindName("HeaderInfo")).Text);
    Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("HeaderRefusal")).Visibility);

    Assert.DoesNotContain(Descendants<Button>(window), button => button.Content is string text && text.Contains("Preview", StringComparison.OrdinalIgnoreCase));
    var tabs = Assert.Single(Descendants<TabControl>(window));
    Assert.Equal(["_Metadata", "_Track order", "_Chapters"], tabs.Items.OfType<TabItem>().Select(tab => tab.Header));
    Assert.Null(window.FindName("BookQuality"));
    Assert.Null(window.FindName("BookOutputTemplate"));
    Assert.Null(window.FindName("BookValidation"));
    Assert.Null(window.FindName("BookChannels"));
    Assert.Null(window.FindName("BookMetadataProfile"));
    _ = other;
  });

  [Fact]
  public void ApplyingAPresetAndChoosingAFolderChangeTheSelectedBookAndAreSaved() => RunWindow(async (window, model, root) =>
  {
    var dataDirectory = Path.Combine(root, "data");
    var store = new QueueSnapshotStore(dataDirectory);
    await model.AttachQueueAsync(store, TimeSpan.FromHours(1), action => action());
    var item = await AddAsync(model, "Made Up Tale");
    var other = await AddAsync(model, "Another Tale");
    var queue = (ListBox)window.FindName("Queue");
    queue.SelectedItem = item;
    Settle(window);

    var picker = (ComboBox)window.FindName("PresetPicker");
    Assert.Equal("Commute", Assert.IsType<PresetEntryViewModel>(Assert.Single(picker.Items)).Name);
    picker.SelectedIndex = 0;
    Invoke((Button)window.FindName("ApplyPresetButton"));
    Assert.Equal("efficient", item.QualityProfileId);
    Assert.Equal(ChannelPolicy.ForceMono, item.ChannelPolicy);
    Assert.Equal("{author}\\{title}", item.OutputTemplate);
    Assert.Equal(AppSettings.Defaults.QualityProfileId, other.QualityProfileId);

    var chosen = Path.Combine(root, "chosen");
    string? offered = null;
    window.ChooseFolder = (_, initial) => { offered = initial; return chosen; };
    Invoke((Button)window.FindName("BrowseDestinationButton"));
    Assert.Equal(item.Settings.OutputDirectory, chosen);
    Assert.Equal(chosen, ((TextBox)window.FindName("BookOutputDirectory")).Text);
    Assert.NotNull(offered);
    Assert.NotEqual(chosen, other.OutputDirectory);

    model.FlushQueue();
    var saved = Assert.IsType<QueueSnapshot>(store.Load().Snapshot).Items;
    Assert.Equal(chosen, saved[0].Settings.OutputDirectory);
    Assert.Equal("efficient", saved[0].Settings.QualityProfileId);
    Assert.Equal(ChannelPolicy.ForceMono, saved[0].Settings.ChannelPolicy);
    Assert.Equal(AppSettings.Defaults.QualityProfileId, saved[1].Settings.QualityProfileId);
  }, presets: [new ConversionPreset("Commute", "efficient", ChannelPolicy.ForceMono, true, ValidationLevel.Lightweight, "GenericMp4", "{author}\\{title}")]);

  [Fact]
  public void TheProfilePickersShowOnlyWhileTheBookIsBlockedAndFixTheBook() => RunWindow(async (window, model, root) =>
  {
    model.Settings = model.Settings with { QualityProfileId = "gone-quality" };
    var item = await AddAsync(model, "Made Up Tale");
    var queue = (ListBox)window.FindName("Queue");
    queue.SelectedItem = item;
    Settle(window);

    var fix = (StackPanel)window.FindName("ProfileFix");
    var quality = (ComboBox)window.FindName("MissingQualityPicker");
    Assert.Equal(Visibility.Visible, fix.Visibility);
    Assert.False(item.CanConvert);

    quality.SelectedValue = "balanced";
    Settle(window);

    Assert.Equal("balanced", item.QualityProfileId);
    Assert.Null(item.BlockReason);
    Assert.True(item.CanConvert);
    Assert.Equal(Visibility.Collapsed, fix.Visibility);
  });

  [Fact]
  public void TheNewSelectedBookControlsHaveAccessKeysNoOtherControlUses() => RunWindow((window, _, _) =>
  {
    var keys = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    void Add(string? text, string where)
    {
      if (text is null) return;
      var index = text.IndexOf('_', StringComparison.Ordinal);
      if (index < 0 || index + 1 >= text.Length) return;
      var key = text[index + 1].ToString();
      if (!keys.TryGetValue(key, out var list)) keys[key] = list = [];
      list.Add(where);
    }
    foreach (var button in Descendants<Button>(window)) Add(button.Content as string, $"button {button.Content}");
    foreach (var tab in Descendants<TabItem>(window)) Add(tab.Header as string, $"tab {tab.Header}");
    foreach (var expander in Descendants<Expander>(window)) Add(expander.Header as string, $"expander {expander.Header}");
    foreach (var label in Descendants<Label>(window)) Add(label.Content as string, $"label {label.Content}");
    foreach (var box in Descendants<CheckBox>(window)) Add(box.Content as string, $"check box {box.Content}");

    foreach (var name in NewButtons)
    {
      var content = (string)((Button)window.FindName(name)).Content;
      var key = content[(content.IndexOf('_', StringComparison.Ordinal) + 1)..][..1];
      Assert.Single(keys[key]);
    }
    return Task.CompletedTask;
  });

  private static MainWindowViewModel Model(FakePlanner planner, string output, IConversionService? converter = null, GuiOperationGate? gate = null)
    => new(new QueueTests.Analyzer(), converter ?? new Converter(ConversionTerminalStatus.Succeeded), AppSettings.Defaults with { OutputDirectory = output }, planner, gate)
    { HeaderDebounce = TimeSpan.Zero, HeaderGateRetry = TimeSpan.FromMilliseconds(10) };

  private static async Task<BookQueueItemViewModel> AddAsync(MainWindowViewModel model, string title, bool ready = true)
  {
    await model.AddAsync(Path.GetFullPath($"header-{model.Items.Count}"));
    var item = model.Items[^1];
    if (ready) await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);
    if (ready) item.Metadata.Single(field => field.Field == SemanticField.BookTitle).Value = title;
    return item;
  }

  private static void RunWindow(Func<MainWindow, MainWindowViewModel, string, Task> check, ConversionPreset[]? presets = null) => DispatcherTestHost.Run(async _ =>
  {
    var root = Directory.CreateTempSubdirectory("booksplice-header-").FullName;
    try
    {
      if (presets is not null) new PresetStore(Path.Combine(root, "BookSplice", PresetStore.FileName)).Save(presets);
      var services = new BookSplice.Gui.Bootstrap.GuiServices(new QueueTests.Analyzer(), new FakePlanner(), new Converter(ConversionTerminalStatus.Succeeded), new NoCovers());
      var window = new MainWindow(new JsonSettingsStore(root), AppSettings.Defaults with { OutputDirectory = Path.Combine(root, "out") }, services)
      { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
      window.Show();
      window.UpdateLayout();
      var model = (MainWindowViewModel)window.DataContext;
      model.HeaderDebounce = TimeSpan.Zero;
      Directory.CreateDirectory(Path.Combine(root, "out"));
      try { await check(window, model, root); }
      finally
      {
        foreach (var item in model.Items) item.IsBusy = false;
        window.Close();
      }
    }
    finally { try { Directory.Delete(root, true); } catch (IOException) { } }
  });

  private static void Settle(Window window)
  {
    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    window.UpdateLayout();
  }

  private static void Invoke(Button button)
  {
    ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    button.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
  }

  private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
  {
    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
    {
      var child = VisualTreeHelper.GetChild(parent, index);
      if (child is T match) yield return match;
      foreach (var descendant in Descendants<T>(child)) yield return descendant;
    }
  }

  private sealed class TempFolder : IDisposable
  {
    public string Path { get; } = Directory.CreateTempSubdirectory("booksplice-header-names-").FullName;
    public void Dispose() => Directory.Delete(Path, true);
  }

  private sealed class NoCovers : ICoverPayloadOpener
  {
    public ValueTask<Stream> OpenReadAsync(CoverPayloadReference payload, CancellationToken cancellationToken) => throw new InvalidOperationException("No cover should be read.");
  }

  private sealed class Converter(ConversionTerminalStatus status) : IConversionService
  {
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
      => Task.FromResult(new ConversionServiceResult(Guid.NewGuid(), status, ConversionStage.Completed, null, null, null, null, null, [], [], status == ConversionTerminalStatus.Succeeded ? "finished.m4b" : null));
  }

  internal sealed class Hold
  {
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
  }

  // Names come from the real name planner, so claimed names show up the way a conversion would see them.
  internal sealed class FakePlanner(AudioStrategy strategy = AudioStrategy.DirectTranscode, string[]? reasons = null, SpaceEstimate? space = null, bool refuse = false, AnalysisDiagnostic[]? notes = null) : IConversionPlanner
  {
    private Hold? _hold;
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public AudioStrategy Strategy { get; set; } = strategy;

    public Hold HoldNext() => _hold = new Hold();

    public async Task<ConversionPlanningResult> CreateAsync(BookAnalysis analysis, ConversionOptions options, CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref _calls);
      if (Interlocked.Exchange(ref _hold, null) is { } hold)
      {
        hold.Started.SetResult();
        await hold.Release.Task;
      }
      var named = ConversionPlanner.PlanOutputPath(new OutputNamePlanner(), analysis, options)!;
      var plan = new ConversionPlan([], analysis.BookMetadata, null, [], options.QualityProfile, options.Settings.ValidationLevel,
        options.CollisionPolicy, named.Path, Strategy, reasons ?? [], space ?? new SpaceEstimate(1, 1, 2, 3, 1), options.Settings.ConversionJobs ?? 6, "test",
        MetadataProfiles.GenericMp4, 44_100, 2);
      return refuse
        ? new ConversionPlanningResult(BookAnalysisStatus.Invalid, null, [new("planning.insufficient-space", AnalysisDiagnosticSeverity.Error, "The destination does not have enough available space.")], plan.Space, Strategy, reasons)
        : new ConversionPlanningResult(BookAnalysisStatus.Ready, plan, notes ?? named.Notes);
    }
  }
}
