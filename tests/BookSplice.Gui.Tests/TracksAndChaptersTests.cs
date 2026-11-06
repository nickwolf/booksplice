using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Settings;
using BookSplice.Gui.Controls;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class TracksAndChaptersTests
{
  private static readonly string[] Natural = ["a.mp3", "b.mp3", "c.mp3"];
  private static readonly string[] Tags = ["c.mp3", "b.mp3", "a.mp3"];

  [Fact]
  public void TheTabHoldsTheOrderControlsAndTheChapterGridAndTheOldTabsAreGone() => RunWindow(async (window, model) =>
  {
    var tabs = Assert.Single(Descendants<TabControl>(window));
    var tab = Assert.Single(tabs.Items.OfType<TabItem>(), candidate => (string)candidate.Header == "_Tracks and chapters");
    var content = (DependencyObject)tab.Content;
    Assert.Contains(Descendants<TextBlock>(content), text => text.Text == "Track order");
    Assert.Same(window.FindName("OrderChoice"), Assert.Single(Descendants<ComboBox>(content)));
    Assert.Equal(2, Descendants<ListBox>(content).Count());
    Assert.Equal(["Move track up", "Move track down", "Exclude track", "Put track back"],
      Descendants<IconButton>(content).Select(button => AutomationProperties.GetName(button)));
    Assert.All(Descendants<IconButton>(content), button => Assert.False(string.IsNullOrWhiteSpace(button.ToolTip as string)));
    Assert.Equal(["Start", "Title", "Source"], Assert.Single(Descendants<DataGrid>(content)).Columns.Select(column => column.Header));
    Assert.DoesNotContain(tabs.Items.OfType<TabItem>(), candidate => candidate.Header is string header && (header.Contains("Track order", StringComparison.Ordinal) || header == "_Chapters"));
    await Task.CompletedTask;
  });

  [Fact]
  public void TheDropdownShowsFriendlyNamesAndNotTheEnumNames() => RunWindow(async (window, model) =>
  {
    var book = await AddAsync(model, "names");
    Select(window, book);
    var combo = (ComboBox)window.FindName("OrderChoice");
    Assert.Equal(["File and folder names", "Track numbers in metadata"], combo.Items.OfType<OrderChoiceViewModel>().Select(choice => choice.Name));
    Assert.Equal("Name", combo.DisplayMemberPath);
  });

  [Fact]
  public void ABookWithNoDecisionShowsThePlaceholderAndStaysBlockedUntilAnOrderIsUsed() => RunWindow(async (window, model) =>
  {
    var book = await AddAsync(model, "undecided");
    Select(window, book);
    var combo = (ComboBox)window.FindName("OrderChoice");
    var placeholder = (TextBlock)window.FindName("OrderPlaceholder");

    Assert.Null(combo.SelectedItem);
    Assert.Equal(Visibility.Visible, placeholder.Visibility);
    Assert.Equal("Choose a track order", placeholder.Text);
    Assert.Equal("Choose a track order", AutomationProperties.GetHelpText(combo));
    Assert.False(book.CanConvert);
    Assert.Empty(book.IncludedTracks);

    await model.ResolveOrderAsync(book, OrderCandidateId.Metadata);
    Settle(window);
    Assert.Equal(OrderCandidateId.Metadata, Assert.IsType<OrderChoiceViewModel>(combo.SelectedItem).Id);
    Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
    Assert.Equal("", AutomationProperties.GetHelpText(combo));
    Assert.Equal(Tags, book.IncludedTracks);
  });

  [Fact]
  public void AResolvedBookPreselectsTheResolvedCandidateWithoutTouchingItsList() => RunWindow(async (window, model) =>
  {
    var book = await AddAsync(model, "resolved", resolved: true);
    Select(window, book);
    var combo = (ComboBox)window.FindName("OrderChoice");

    Assert.Equal(OrderCandidateId.NaturalPath, Assert.IsType<OrderChoiceViewModel>(combo.SelectedItem).Id);
    Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("OrderPlaceholder")).Visibility);
    Assert.Equal(Natural, book.IncludedTracks);
  });

  [Fact]
  public async Task ACandidateTheUserChoseWinsOverTheResolvedOne()
  {
    var model = new MainWindowViewModel(new OrderAnalyzer { Resolved = true }, new NoConverter(), AppSettings.Defaults);
    await model.AddAsync(Path.GetFullPath("chosen"));
    var book = model.Items[0];
    Assert.Equal(OrderCandidateId.NaturalPath, book.PreselectedOrder);

    book.SelectedOrder = OrderCandidateId.Metadata;

    Assert.Equal(OrderCandidateId.Metadata, book.PreselectedOrder);
  }

  [Fact]
  public void SelectingABookWithARestoredReviewedOrderKeepsItsListsAndEdits() => RunWindow(async (window, model) =>
  {
    var other = await AddAsync(model, "other", resolved: true);
    var restored = await AddAsync(model, "restored", resolved: true);
    restored.ExcludeTrack("b.mp3");
    await model.ApplyTrackListAsync(restored);
    restored.MoveTrack("c.mp3", -1);
    Assert.True(restored.IsTrackListEdited);
    Select(window, other);

    Select(window, restored);

    Assert.Equal(["c.mp3", "a.mp3"], restored.IncludedTracks);
    Assert.Equal(["b.mp3"], restored.ExcludedTracks);
    Assert.True(restored.IsTrackListEdited);
    Assert.NotNull(restored.ReviewedOrder);
    Assert.Equal(OrderCandidateId.NaturalPath, Assert.IsType<OrderChoiceViewModel>(((ComboBox)window.FindName("OrderChoice")).SelectedItem).Id);
  });

  [Fact]
  public void SwitchingBetweenBooksLeavesBothTrackListsAlone() => RunWindow(async (window, model) =>
  {
    var first = await AddAsync(model, "first", resolved: true);
    var second = await AddAsync(model, "second", resolved: true);
    first.MoveTrack("a.mp3", 2);
    second.ExcludeTrack("c.mp3");
    second.MoveTrack("b.mp3", 1);

    Select(window, first);
    Select(window, second);
    Select(window, first);
    Select(window, second);

    Assert.Equal(["b.mp3", "c.mp3", "a.mp3"], first.IncludedTracks);
    Assert.Empty(first.ExcludedTracks);
    Assert.True(first.IsTrackListEdited);
    Assert.Equal(["a.mp3", "b.mp3"], second.IncludedTracks);
    Assert.Equal(["c.mp3"], second.ExcludedTracks);
    Assert.True(second.IsTrackListEdited);
  });

  [Fact]
  public void AUserPickingTheOtherCandidateLoadsItsFileOrder() => RunWindow(async (window, model) =>
  {
    var book = await AddAsync(model, "pick", resolved: true);
    book.ExcludeTrack("a.mp3");
    Select(window, book);
    var combo = (ComboBox)window.FindName("OrderChoice");

    combo.SelectedItem = combo.Items.OfType<OrderChoiceViewModel>().Single(choice => choice.Id == OrderCandidateId.Metadata);

    Assert.Equal(Tags, book.IncludedTracks);
    Assert.Empty(book.ExcludedTracks);
    Assert.False(book.IsTrackListEdited);
  });

  [Fact]
  public void TheTrackButtonsAreDisabledWhenTheyCannotAct() => RunWindow(async (window, model) =>
  {
    var book = await AddAsync(model, "buttons", resolved: true);
    Select(window, book);
    var included = (ListBox)window.FindName("IncludedList");
    var excluded = (ListBox)window.FindName("ExcludedList");
    bool Enabled(string name) => ((Button)window.FindName(name)).IsEnabled;

    Assert.False(Enabled("TrackUpButton") || Enabled("TrackDownButton") || Enabled("TrackExcludeButton") || Enabled("TrackIncludeButton"));

    included.SelectedIndex = 0;
    Assert.False(Enabled("TrackUpButton"));
    Assert.True(Enabled("TrackDownButton") && Enabled("TrackExcludeButton"));
    included.SelectedIndex = 2;
    Assert.True(Enabled("TrackUpButton"));
    Assert.False(Enabled("TrackDownButton"));

    book.ExcludeTrack("c.mp3");
    Assert.False(Enabled("TrackDownButton"));
    excluded.SelectedIndex = 0;
    Assert.True(Enabled("TrackIncludeButton"));
  });

  [Fact]
  public void NoTwoControlsInTheWholeWindowShareAnAccessKey() => RunWindow(async (window, model) =>
  {
    var book = await AddAsync(model, "keys", resolved: true);
    Select(window, book);
    var pause = (UIElement)window.FindName("PauseQueueButton");
    var entries = new List<(string Key, string Source)>();
    foreach (var element in Logical(window))
    {
      if (element == pause) continue;
      var text = element switch
      {
        ContentControl { Content: string content } => content,
        HeaderedContentControl { Header: string header } => header,
        _ => null,
      };
      if (AccessKey(text) is { } key) entries.Add((key, $"{element.GetType().Name} \"{text}\""));
    }

    // Pause and Resume are one button in two states, so they take one key between them.
    var pauseKey = AccessKey(model.QueuePauseLabel)!;
    entries.Add((pauseKey, "Pause queue"));
    model.PauseQueue();
    Assert.Equal(pauseKey, AccessKey(model.QueuePauseLabel));

    Assert.Contains(entries, entry => entry.Source.Contains("Tracks and chapters", StringComparison.Ordinal));
    Assert.Contains(entries, entry => entry.Source.Contains("Convert ready", StringComparison.Ordinal));
    Assert.Contains(entries, entry => entry.Source.Contains("Validation", StringComparison.Ordinal));
    var shared = entries.GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)
      .Select(group => $"{group.Key}: {string.Join(", ", group.Select(entry => entry.Source))}").ToArray();
    Assert.True(shared.Length == 0, string.Join(Environment.NewLine, shared));
    await Task.CompletedTask;
  });

  private static string? AccessKey(string? text)
  {
    var index = text?.IndexOf('_', StringComparison.Ordinal) ?? -1;
    return index >= 0 && index + 1 < text!.Length ? text[index + 1].ToString().ToUpperInvariant() : null;
  }

  private static IEnumerable<object> Logical(DependencyObject parent)
  {
    yield return parent;
    foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
      foreach (var descendant in Logical(child)) yield return descendant;
  }

  private static async Task<BookQueueItemViewModel> AddAsync(MainWindowViewModel model, string name, bool resolved = false)
  {
    await model.AddAsync(Path.GetFullPath(name));
    var item = model.Items[^1];
    if (resolved) await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);
    return item;
  }

  private static void Select(MainWindow window, BookQueueItemViewModel item)
  {
    ((ListBox)window.FindName("Queue")).SelectedItem = item;
    Settle(window);
  }

  private static void Settle(Window window)
  {
    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    window.UpdateLayout();
  }

  private static void RunWindow(Func<MainWindow, MainWindowViewModel, Task> check) => DispatcherTestHost.Run(async _ =>
  {
    var root = Directory.CreateTempSubdirectory("booksplice-tracks-").FullName;
    try
    {
      var services = new BookSplice.Gui.Bootstrap.GuiServices(new OrderAnalyzer(), new BookHeaderTests.FakePlanner(), new NoConverter(), new NoCovers());
      var window = new MainWindow(new JsonSettingsStore(root), AppSettings.Defaults with { OutputDirectory = Path.Combine(root, "out") }, services)
      { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
      window.Show();
      window.UpdateLayout();
      var model = (MainWindowViewModel)window.DataContext;
      model.HeaderDebounce = TimeSpan.Zero;
      try { await check(window, model); }
      finally
      {
        foreach (var item in model.Items) item.IsBusy = false;
        window.Close();
      }
    }
    finally { try { Directory.Delete(root, true); } catch (IOException) { } }
  });

  private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
  {
    for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
    {
      var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
      if (child is T match) yield return match;
      foreach (var descendant in Descendants<T>(child)) yield return descendant;
    }
  }

  private static SourceFile File(string root, string name) => new(Path.Combine(root, name), name,
    new MediaProbeResult([new AudioTrack(0, "mp3", "audio", 1, 44100, 1m, new Rational(1, 44100), new Dictionary<string, string>())], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1m),
    new SourceIdentity(Path.Combine(root, name), 1, DateTime.UnixEpoch, name));

  // Two credible candidates; undecided until one is chosen, unless Resolved picks the natural one.
  private sealed class OrderAnalyzer : IBookAnalyzer
  {
    public bool Resolved { get; init; }

    public Task<BookAnalysis> AnalyzeAsync(SourceSelection selection, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default) => AnalyzeAsync(selection.RootPath, chaptersEnabled, options, cancellationToken);

    public Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      var natural = new OrderCandidate(OrderCandidateId.NaturalPath, Natural, true, OrderConfidence.High, []);
      var tags = new OrderCandidate(OrderCandidateId.Metadata, Tags, true, OrderConfidence.High, []);
      var chosen = options?.SelectedOrderCandidateId is { } id ? (id == OrderCandidateId.Metadata ? tags : natural) : Resolved ? natural : null;
      var reviewed = options?.ReviewedOrder;
      IEnumerable<string> included = reviewed?.Included ?? chosen?.FileIds ?? [];
      var metadata = new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
      return Task.FromResult(new BookAnalysis(chosen is null ? BookAnalysisStatus.NeedsDecision : BookAnalysisStatus.Ready, inputPath, "book", included.Select(name => File(inputPath, name)),
        new OrderResolution(chosen is null ? OrderStatus.NeedsDecision : OrderStatus.Resolved, chosen, [natural, tags], []), metadata,
        new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([], 0), [], (reviewed?.Excluded ?? []).Select(name => File(inputPath, name)), Natural));
    }
  }

  private sealed class NoCovers : ICoverPayloadOpener
  {
    public ValueTask<Stream> OpenReadAsync(CoverPayloadReference payload, CancellationToken cancellationToken) => throw new InvalidOperationException("No cover should be read.");
  }

  private sealed class NoConverter : IConversionService
  {
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
      => throw new InvalidOperationException("No conversion expected.");
  }
}
