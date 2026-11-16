using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.EditCopy;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Settings;
using BookSplice.Core.Validation;
using BookSplice.Gui.Bootstrap;
using BookSplice.Gui.ViewModels;
using BookSplice.Gui.Views;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class EditCopyTests
{
  private static readonly string BookPath = Path.GetFullPath("edit-book.m4b");

  [Fact]
  public void AnInspectOnlyBookCanBeEditedButAReadyBookCannotAndNeitherCanWhileBusy() => DispatcherTestHost.Run(async _ =>
  {
    var service = new FakeEditCopy(Published());
    using var model = Model(service, new FakeLog(), "out");
    var item = await Add(model);
    Assert.True(item.CanEditCopy);
    Assert.True(item.CanCheck);
    Assert.NotNull(model.PrepareEditCopy(item));

    item.IsBusy = true;
    Assert.False(item.CanEditCopy);
    Assert.False(item.CanCheck);
    Assert.Null(model.PrepareEditCopy(item));
    item.IsBusy = false;
    item.IsWaiting = true;
    Assert.False(item.CanEditCopy);
    item.IsWaiting = false;
    Assert.True(item.CanEditCopy);

    await model.AddAsync(Path.GetFullPath("ready-book"));
    var ready = model.Items[^1];
    await model.ResolveOrderAsync(ready, OrderCandidateId.NaturalPath);
    Assert.True(ready.CanConvert);
    Assert.False(ready.CanEditCopy);
    Assert.Null(model.PrepareEditCopy(ready));
  });

  [Fact]
  public void TheModelStartsWithTheFilesTagsAndChapterTitlesAndNothingToCreate() => DispatcherTestHost.Run(async _ =>
  {
    using var model = Model(new FakeEditCopy(Published()), new FakeLog(), "out");
    var edits = Prepare(model, await Add(model));

    Assert.Equal(["Title", "Author", "Narrator", "Genre", "Year", "Comment", "Description", "Copyright", "Language", "iTunes media type"], edits.Fields.Select(field => field.Label));
    Assert.Equal("Made Up Tale", edits.Fields.Single(field => field.Field == SemanticField.BookTitle).Value);
    Assert.Equal("Some Author", edits.Fields.Single(field => field.Field == SemanticField.Author).Value);
    Assert.Equal("", edits.Fields.Single(field => field.Field == SemanticField.Genre).Value);
    Assert.Equal(["One", "Two", "Three"], edits.Chapters.Select(chapter => chapter.Title));
    Assert.Equal(["0:00:00", "0:01:00", "0:02:05"], edits.Chapters.Select(chapter => chapter.Start));
    Assert.Equal(["1", "2", "3"], edits.Chapters.Select(chapter => chapter.Number));
    Assert.False(edits.HasChanges);
    Assert.False(edits.CanCreate);
    Assert.Empty(edits.Preview);
  });

  [Fact]
  public void OnlyChangedFieldsAndTitlesGoIntoTheRequestAndAClearedFieldIsSentEmpty() => DispatcherTestHost.Run(async _ =>
  {
    var service = new FakeEditCopy(Published());
    using var model = Model(service, new FakeLog(), "out");
    var item = await Add(model);
    var edits = Prepare(model, item);
    edits.Fields.Single(field => field.Field == SemanticField.BookTitle).Value = "A Better Tale";
    edits.Fields.Single(field => field.Field == SemanticField.Narrator).Value = "";
    edits.Fields.Single(field => field.Field == SemanticField.Genre).Value = "Fantasy";
    edits.Fields.Single(field => field.Field == SemanticField.Author).Value = "Other";
    edits.Fields.Single(field => field.Field == SemanticField.Author).Value = "Some Author";
    edits.Chapters[1].Title = "Second";

    Assert.Equal(["Title: \"Made Up Tale\" -> \"A Better Tale\"", "Narrator: \"Some Narrator\" -> (removed)", "Genre: (none) -> \"Fantasy\"", "Chapter 2: \"Two\" -> \"Second\""], edits.Preview);
    Assert.True(edits.CanCreate);

    await model.EditCopyAsync(item, edits);

    var request = Assert.Single(service.Requests);
    Assert.Equal(BookPath, request.SourcePath);
    Assert.Equal(new Dictionary<SemanticField, string> { [SemanticField.BookTitle] = "A Better Tale", [SemanticField.Narrator] = "", [SemanticField.Genre] = "Fantasy" }, request.TagEdits);
    Assert.Equal(new Dictionary<int, string> { [1] = "Second" }, request.ChapterTitleEdits);
    Assert.Equal("GenericMp4", request.Profile.Name);
    Assert.Equal(EditCopyPreview.Describe(Probe(), request), edits.Preview);
  });

  [Fact]
  public void CreateStaysOffWithoutChangesOrWithABlankTitleAndTheMethodDoesNothing() => DispatcherTestHost.Run(async _ =>
  {
    var service = new FakeEditCopy(Published());
    var log = new FakeLog();
    using var model = Model(service, log, "out");
    var item = await Add(model);
    var edits = Prepare(model, item);

    await model.EditCopyAsync(item, edits);
    Assert.Empty(service.Requests);
    Assert.Equal("M4B inspection only", item.Status);

    var title = edits.Fields.Single(field => field.Field == SemanticField.BookTitle);
    title.Value = "Changed";
    Assert.True(edits.CanCreate);
    title.Value = "Made Up Tale";
    Assert.False(edits.CanCreate);
    title.Value = "Changed";

    edits.Chapters[2].Title = "";
    Assert.True(edits.Chapters[2].IsInvalid);
    Assert.True(edits.HasInvalidTitle);
    Assert.False(edits.CanCreate);
    Assert.Equal("Every chapter needs a title.", edits.ProblemText);
    edits.Chapters[2].Title = "   ";
    Assert.False(edits.CanCreate);
    await model.EditCopyAsync(item, edits);
    Assert.Empty(service.Requests);
    Assert.Empty(log.Records);

    edits.Chapters[2].Title = "Three";
    Assert.False(edits.HasInvalidTitle);
    Assert.True(edits.CanCreate);
    Assert.Equal("", edits.ProblemText);
  });

  [Fact]
  public void APublishedCopyShowsItsChecksSetsThePublishedPathAndWritesAnEditRecord() => DispatcherTestHost.Run(async _ =>
  {
    var root = Directory.CreateTempSubdirectory("booksplice-edit-").FullName;
    try
    {
      var copy = Path.Combine(root, "edit-book.m4b");
      await File.WriteAllTextAsync(copy, "audio");
      var service = new FakeEditCopy(new EditCopyResult(EditCopyStatus.Published, copy, [new("edit.note", "Cover was kept.")],
        [new("edit.audio-packets", true, "The audio is unchanged."), new("edit.chapters-titles", true, "Titles match."), new("edit.cover", false, "Not required here.", false)]));
      var log = new FakeLog();
      using var model = Model(service, log, root);
      var item = await Add(model);
      var edits = Prepare(model, item);
      edits.Fields.Single(field => field.Field == SemanticField.BookTitle).Value = "Changed";

      await model.EditCopyAsync(item, edits);

      Assert.Equal("Edited copy", item.Status);
      Assert.Equal("Edited copy", item.QueueStatus);
      Assert.False(item.IsBusy);
      Assert.True(item.HasCheckResult);
      Assert.False(item.HasConversionResult);
      Assert.Equal(copy, item.PublishedPath);
      Assert.True(item.CanOpenOutput);
      Assert.True(item.CanEditCopy);
      Assert.Equal(["Passed: edit.audio-packets", "Passed: edit.chapters-titles", "Not required: edit.cover. Not required here.", "Cover was kept."], item.Result!.CheckLines);
      Assert.Equal($"Saved to: {copy}", item.Result.EditOutputText);
      var record = Assert.Single(log.Records);
      Assert.Equal("edit", record.Kind);
      Assert.Equal(ConversionTerminalStatus.Succeeded, record.TerminalStatus);
      Assert.Equal(copy, record.PublishedPath);
      Assert.Null(record.CheckedPath);
      Assert.Equal(["edit.audio-packets", "edit.chapters-titles", "edit.cover", "edit.note"], record.Diagnostics.Select(value => value.Code));
      Assert.All(record.Diagnostics, value => Assert.Equal(ServiceDiagnosticSeverity.Information, value.Severity));
    }
    finally { try { Directory.Delete(root, true); } catch (IOException) { } }
  });

  [Fact]
  public void AFailedValidationIsShownAndLoggedAsValidationFailed() => DispatcherTestHost.Run(async _ =>
  {
    var service = new FakeEditCopy(new EditCopyResult(EditCopyStatus.ValidationFailed, null, null,
      [new("edit.audio-packets", true, "The audio is unchanged."), new("edit.chapters-timing", false, "Chapter 2 moved.")]));
    var log = new FakeLog();
    using var model = Model(service, log, "out");
    var item = await Add(model);
    var edits = Prepare(model, item);
    edits.Chapters[0].Title = "Opening";

    await model.EditCopyAsync(item, edits);

    Assert.Equal("Edit failed", item.Status);
    Assert.Equal("Edit failed", item.QueueStatus);
    Assert.Null(item.PublishedPath);
    Assert.Equal(["Passed: edit.audio-packets", "Failed: edit.chapters-timing. Chapter 2 moved."], item.Result!.CheckLines);
    Assert.Equal("No copy was saved.", item.Result.EditOutputText);
    var record = Assert.Single(log.Records);
    Assert.Equal("edit", record.Kind);
    Assert.Equal(ConversionTerminalStatus.ValidationFailed, record.TerminalStatus);
    Assert.Null(record.PublishedPath);
    Assert.Equal([ServiceDiagnosticSeverity.Information, ServiceDiagnosticSeverity.Error], record.Diagnostics.Select(value => value.Severity));
    Assert.True(item.CanEditCopy);
  });

  [Fact]
  public void AFailedEditIsShownAndLoggedWithItsDiagnostics() => DispatcherTestHost.Run(async _ =>
  {
    var service = new FakeEditCopy(new EditCopyResult(EditCopyStatus.Failed, null, [new(EditCopyCodes.ProcessFailed, "ffmpeg exited with code 1")]));
    var log = new FakeLog();
    using var model = Model(service, log, "out");
    var item = await Add(model);
    var edits = Prepare(model, item);
    edits.Chapters[0].Title = "Opening";

    await model.EditCopyAsync(item, edits);

    Assert.Equal("Edit failed", item.Status);
    Assert.Equal(["ffmpeg exited with code 1"], item.Result!.CheckLines);
    var record = Assert.Single(log.Records);
    Assert.Equal(ConversionTerminalStatus.ExecutionFailed, record.TerminalStatus);
    var diagnostic = Assert.Single(record.Diagnostics);
    Assert.Equal(ServiceDiagnosticSeverity.Error, diagnostic.Severity);
    Assert.Equal("ffmpeg exited with code 1", diagnostic.Message);
  });

  [Fact]
  public void TheBookIsBusyWhileEditingAndCancelReturnsItToInspectOnlyWithACancelledRecord() => DispatcherTestHost.Run(async dispatcher =>
  {
    var service = new FakeEditCopy(Published()) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
    var log = new FakeLog();
    using var model = Model(service, log, "out");
    var item = await Add(model);
    var edits = Prepare(model, item);
    edits.Chapters[0].Title = "Opening";

    var running = model.EditCopyAsync(item, edits);
    await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.True(item.IsBusy);
    Assert.False(item.CanEditCopy);
    Assert.False(item.CanCheck);
    Assert.False(item.CanRemove);
    Assert.True(item.CanCancel);
    Assert.Equal("Editing", item.Status);
    service.Progress!.Report(new ConversionProgress(null, null, null, 0, Phase: ConversionPhase.FullDecode, PhaseOccurrenceId: Guid.NewGuid(), IsPhaseEntry: true, Sequence: 1));
    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    Assert.Contains("Validating full audio", item.Details, StringComparison.Ordinal);

    item.Cancel();
    Assert.True(service.Token.IsCancellationRequested);
    await running;

    Assert.Equal("Edit cancelled", item.Status);
    Assert.False(item.IsBusy);
    Assert.True(item.CanEditCopy);
    Assert.False(item.HasResult);
    var record = Assert.Single(log.Records);
    Assert.Equal(ConversionTerminalStatus.Cancelled, record.TerminalStatus);
    Assert.Equal("edit", record.Kind);
    Assert.Null(record.PublishedPath);
  });

  [Fact]
  public void ACancelledResultFromTheServiceIsTreatedLikeACancellation() => DispatcherTestHost.Run(async _ =>
  {
    var log = new FakeLog();
    using var model = Model(new FakeEditCopy(new EditCopyResult(EditCopyStatus.Cancelled, null, [new(EditCopyCodes.Cancelled, "Cancelled.")])), log, "out");
    var item = await Add(model);
    var edits = Prepare(model, item);
    edits.Chapters[0].Title = "Opening";

    await model.EditCopyAsync(item, edits);

    Assert.Equal("Edit cancelled", item.Status);
    Assert.False(item.HasResult);
    Assert.Equal(ConversionTerminalStatus.Cancelled, Assert.Single(log.Records).TerminalStatus);
  });

  [Fact]
  public void AnUnexpectedExceptionIsShownOnTheBookAndNotLogged() => DispatcherTestHost.Run(async _ =>
  {
    var log = new FakeLog();
    using var model = Model(new FakeEditCopy(Published()) { Failure = new InvalidOperationException("disk exploded") }, log, "out");
    var item = await Add(model);
    var edits = Prepare(model, item);
    edits.Chapters[0].Title = "Opening";

    await model.EditCopyAsync(item, edits);

    Assert.Equal("Edit failed", item.Status);
    Assert.Contains("disk exploded", item.Details, StringComparison.Ordinal);
    Assert.False(item.IsBusy);
    Assert.False(item.HasResult);
    Assert.Empty(log.Records);
    Assert.True(item.CanEditCopy);
  });

  [Fact]
  public void TheCopyGoesToTheOutputFolderUnderTheOriginalsNameAndNeverOntoTheOriginal() => DispatcherTestHost.Run(async _ =>
  {
    var elsewhere = Path.GetFullPath("elsewhere-folder");
    var sameFolder = Path.GetDirectoryName(BookPath)!;
    var cases = new[]
    {
      (Folder: elsewhere, Expected: Path.Combine(elsewhere, "edit-book.m4b")),
      (Folder: sameFolder, Expected: Path.Combine(sameFolder, "edit-book (edited).m4b")),
      (Folder: sameFolder.ToUpperInvariant(), Expected: Path.Combine(sameFolder.ToUpperInvariant(), "edit-book (edited).m4b")),
      (Folder: Path.Combine(sameFolder, "..", Path.GetFileName(sameFolder)), Expected: Path.Combine(sameFolder, "edit-book (edited).m4b")),
    };
    foreach (var (folder, expected) in cases)
    {
      var service = new FakeEditCopy(Published());
      using var model = Model(service, new FakeLog(), folder);
      var item = await Add(model);
      var edits = Prepare(model, item);
      edits.Chapters[0].Title = "Opening";
      await model.EditCopyAsync(item, edits);
      Assert.Equal(expected, Assert.Single(service.Requests).DestinationPath, StringComparer.OrdinalIgnoreCase);
    }
  });

  [Fact]
  public void ABookWhoseMetadataProfileIsGoneIsRefusedWithAMessage() => DispatcherTestHost.Run(async _ =>
  {
    var service = new FakeEditCopy(Published());
    using var model = new MainWindowViewModel(new EditAnalyzer(), new Idle(), AppSettings.Defaults with { MetadataProfileId = "SavedButGone" }, checkLog: new FakeLog(), editCopy: service);
    var item = await Add(model);
    Assert.True(item.CanEditCopy);
    Assert.Null(model.PrepareEditCopy(item));
    Assert.Contains("metadata profile", model.Message, StringComparison.Ordinal);
    Assert.Empty(service.Requests);
  });

  [Fact]
  public void TheDialogStartsFilledInAndTypingChangesThePreviewAndTheCreateButton() => RunDialog((window, edits) =>
  {
    window.Show();
    window.UpdateLayout();
    var title = Box(window, "Title");
    Assert.Equal("Made Up Tale", title.Text);
    Assert.Equal("Some Author", Box(window, "Author").Text);
    Assert.Equal("", Box(window, "Genre").Text);
    var chapterBoxes = Descendants<TextBox>(window).Where(box => AutomationProperties.GetName(box).StartsWith("Title of chapter", StringComparison.Ordinal)).ToArray();
    Assert.Equal(["One", "Two", "Three"], chapterBoxes.Select(box => box.Text));
    var create = (Button)window.FindName("CreateButton");
    var preview = (ItemsControl)window.FindName("PreviewList");
    Assert.False(create.IsEnabled);
    Assert.Empty(preview.Items);
    Assert.Equal("Nothing has changed yet.", ((TextBlock)window.FindName("PreviewPlaceholder")).Text);

    title.Text = "Typed Title";
    Settle(window);
    Assert.Equal(["Title: \"Made Up Tale\" -> \"Typed Title\""], preview.Items.Cast<string>());
    Assert.True(create.IsEnabled);
    Assert.Equal("", ((TextBlock)window.FindName("PreviewPlaceholder")).Text);

    chapterBoxes[1].Text = "";
    Settle(window);
    Assert.False(create.IsEnabled);
    Assert.Equal("Every chapter needs a title.", ((TextBlock)window.FindName("ProblemText")).Text);
    Assert.Equal(2, preview.Items.Count);
    chapterBoxes[1].Text = "Second";
    Settle(window);
    Assert.True(create.IsEnabled);
    Assert.Equal(["Title: \"Made Up Tale\" -> \"Typed Title\"", "Chapter 2: \"Two\" -> \"Second\""], preview.Items.Cast<string>());

    title.Text = "Made Up Tale";
    chapterBoxes[1].Text = "Two";
    Settle(window);
    Assert.False(create.IsEnabled);
    Assert.Empty(preview.Items);
    window.Close();
  });

  [Fact]
  public void TheDialogControlsHaveUniqueAccessKeys() => RunDialog((window, edits) =>
  {
    window.Show();
    window.UpdateLayout();
    var buttons = Descendants<Button>(window).ToArray();
    var keys = buttons.Select(button => ((string)button.Content)[(((string)button.Content).IndexOf('_', StringComparison.Ordinal) + 1)..][..1].ToUpperInvariant()).ToArray();
    Assert.Equal(2, keys.Length);
    Assert.Equal(keys.Length, keys.Distinct().Count());
    Assert.All(buttons, button => Assert.False(string.IsNullOrWhiteSpace(new ButtonAutomationPeer(button).GetAccessKey())));
    Assert.True(((Button)window.FindName("CancelButton")).IsCancel);
  });

  [Fact]
  public void CancelClosesTheModalDialogWithoutAResultAndCreateAcceptsIt() => DispatcherTestHost.Run(_ =>
  {
    var cancelled = new EditCopyWindow("Made Up Tale", new EditCopyViewModel(BookPath, Probe(), MetadataProfiles.GenericMp4)) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
    cancelled.Dispatcher.BeginInvoke(() =>
    {
      cancelled.Model.Chapters[0].Title = "Changed";
      Invoke((Button)cancelled.FindName("CancelButton"));
    }, DispatcherPriority.ContextIdle);
    Assert.False(cancelled.ShowDialog());

    var created = new EditCopyWindow("Made Up Tale", new EditCopyViewModel(BookPath, Probe(), MetadataProfiles.GenericMp4)) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
    created.Dispatcher.BeginInvoke(() =>
    {
      created.Model.Chapters[0].Title = "Changed";
      Invoke((Button)created.FindName("CreateButton"));
    }, DispatcherPriority.ContextIdle);
    Assert.True(created.ShowDialog());
    Assert.Equal(new Dictionary<int, string> { [0] = "Changed" }, created.Model.CreateRequest(BookPath).ChapterTitleEdits);
    return Task.CompletedTask;
  });

  [Fact]
  public void ThePanelShowsEditACopyOnlyForAnInspectOnlyBookAndTheDialogResultDecidesWhetherAnythingRuns() => RunMain(async (window, model, service, root) =>
  {
    var inspect = await Add(model);
    await model.AddAsync(Path.GetFullPath("ready-book"));
    var ready = model.Items[^1];
    await model.ResolveOrderAsync(ready, OrderCandidateId.NaturalPath);
    var edit = (Button)window.FindName("EditCopySelectedButton");

    Select(window, inspect);
    Assert.Equal(Visibility.Visible, edit.Visibility);
    Assert.Equal("Edit a c_opy", edit.Content);
    Select(window, ready);
    Assert.Equal(Visibility.Collapsed, edit.Visibility);
    Assert.Equal(Visibility.Visible, ((Button)window.FindName("ConvertSelectedButton")).Visibility);
    Select(window, inspect);

    // Cancelling the dialog runs nothing.
    window.ShowEditCopy = dialog =>
    {
      dialog.Model.Chapters[0].Title = "Changed but cancelled";
      return false;
    };
    Invoke(edit);
    Assert.Empty(service.Requests);
    Assert.Equal("M4B inspection only", inspect.Status);

    // Creating runs the edit on the book and shows its result with a way to open the copy.
    window.ShowEditCopy = dialog =>
    {
      Assert.Same(window, dialog.Owner);
      dialog.Model.Chapters[0].Title = "Opening";
      return true;
    };
    Invoke(edit);
    for (var attempt = 0; attempt < 200 && inspect.Status != "Edited copy"; attempt++) await Task.Delay(50);
    Settle(window);

    Assert.Equal("Edited copy", inspect.Status);
    var request = Assert.Single(service.Requests);
    Assert.Equal(new Dictionary<int, string> { [0] = "Opening" }, request.ChapterTitleEdits);
    Assert.Equal(Visibility.Visible, ((Border)window.FindName("CheckResultCard")).Visibility);
    Assert.Equal(Visibility.Collapsed, ((Border)window.FindName("ConversionResultCard")).Visibility);
    Assert.Equal("Edited copy", ((TextBlock)window.FindName("CheckResultTitle")).Text);
    Assert.Equal(Visibility.Visible, ((Button)window.FindName("OpenEditOutput")).Visibility);
    Assert.Equal(Visibility.Visible, edit.Visibility);
    Assert.Equal(Visibility.Visible, ((Button)window.FindName("CheckSelectedButton")).Visibility);
    _ = root;
  });

  private static void RunDialog(Action<EditCopyWindow, EditCopyViewModel> check) => DispatcherTestHost.Run(_ =>
  {
    var edits = new EditCopyViewModel(BookPath, Probe(), MetadataProfiles.GenericMp4);
    var window = new EditCopyWindow("Made Up Tale", edits) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
    try { check(window, edits); }
    finally { window.Close(); }
    return Task.CompletedTask;
  });

  private static void RunMain(Func<MainWindow, MainWindowViewModel, FakeEditCopy, string, Task> check) => DispatcherTestHost.Run(async _ =>
  {
    var root = Directory.CreateTempSubdirectory("booksplice-editcopy-").FullName;
    try
    {
      var copy = Path.Combine(root, "out", "edit-book.m4b");
      Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
      await File.WriteAllTextAsync(copy, "audio");
      var service = new FakeEditCopy(new EditCopyResult(EditCopyStatus.Published, copy, null, [new("edit.audio-packets", true, "The audio is unchanged.")]));
      var services = new GuiServices(new EditAnalyzer(), new BookHeaderTests.FakePlanner(), new Idle(), new NoCovers(), null, null, new FakeLog(), service);
      var window = new MainWindow(new JsonSettingsStore(root), AppSettings.Defaults with { OutputDirectory = Path.Combine(root, "out") }, services)
      { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, Width = 1120, Height = 760 };
      window.Show();
      try { await check(window, (MainWindowViewModel)window.DataContext, service, root); }
      finally { window.Close(); }
    }
    finally { try { Directory.Delete(root, true); } catch (IOException) { } }
  });

  private static TextBox Box(Window window, string name) => Descendants<TextBox>(window).Single(box => AutomationProperties.GetName(box) == name);

  private static void Settle(Window window)
  {
    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    window.UpdateLayout();
  }

  private static void Select(MainWindow window, BookQueueItemViewModel item)
  {
    ((ListBox)window.FindName("Queue")).SelectedItem = item;
    Settle(window);
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

  private static MainWindowViewModel Model(FakeEditCopy service, FakeLog log, string output)
    => new(new EditAnalyzer(), new Idle(), AppSettings.Defaults with { OutputDirectory = output }, checkLog: log, editCopy: service);

  private static async Task<BookQueueItemViewModel> Add(MainWindowViewModel model)
  {
    await model.AddAsync(BookPath);
    return model.Items[^1];
  }

  private static EditCopyViewModel Prepare(MainWindowViewModel model, BookQueueItemViewModel item) => Assert.IsType<EditCopyViewModel>(model.PrepareEditCopy(item));

  private static EditCopyResult Published() => new(EditCopyStatus.Published, Path.GetFullPath("edited-copy.m4b"), null, [new("edit.audio-packets", true, "The audio is unchanged.")]);

  private static MediaProbeResult Probe() => new([], [],
    [
      new MediaChapter(0, 0m, 60m, null, new Dictionary<string, string> { ["title"] = "One" }),
      new MediaChapter(1, 60m, 125m, null, new Dictionary<string, string> { ["title"] = "Two" }),
      new MediaChapter(2, 125m, 200m, null, new Dictionary<string, string> { ["title"] = "Three" }),
    ],
    new TagCollection(new Dictionary<string, string> { ["title"] = "Made Up Tale", ["artist"] = "Some Author", ["composer"] = "Some Narrator" }),
    new Dictionary<string, string>(), []);

  private sealed class FakeEditCopy(EditCopyResult result) : IEditCopyService
  {
    public List<EditCopyRequest> Requests { get; } = [];
    public TaskCompletionSource? Gate { get; init; }
    public Exception? Failure { get; init; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IProgress<ConversionProgress>? Progress { get; private set; }
    public CancellationToken Token { get; private set; }

    public async Task<EditCopyResult> CreateAsync(EditCopyRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      Requests.Add(request);
      Progress = progress;
      Token = cancellationToken;
      Entered.TrySetResult();
      if (Failure is not null) throw Failure;
      if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
      return result;
    }
  }

  private sealed class FakeLog : IJobLogWriter
  {
    public List<ConversionAuditRecord> Records { get; } = [];
    public Task WriteAsync(ConversionAuditRecord record, CancellationToken cancellationToken)
    {
      Records.Add(record);
      return Task.CompletedTask;
    }
  }

  // A single .m4b is inspect-only and carries its probe. Anything else is a ready book.
  private sealed class EditAnalyzer : IBookAnalyzer
  {
    private readonly QueueTests.Analyzer _other = new();

    public Task<BookAnalysis> AnalyzeAsync(SourceSelection selection, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
      => AnalyzeAsync(selection.RootPath, chaptersEnabled, options, cancellationToken);

    public Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      if (!inputPath.EndsWith(".m4b", StringComparison.OrdinalIgnoreCase)) return _other.AnalyzeAsync(inputPath, chaptersEnabled, options, cancellationToken);
      return Task.FromResult(new BookAnalysis(BookAnalysisStatus.InspectOnly, inputPath, [new SourceFile(inputPath, Path.GetFileName(inputPath), Probe())], null,
        new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()),
        new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([], 0), []));
    }
  }

  private sealed class NoCovers : ICoverPayloadOpener
  {
    public ValueTask<Stream> OpenReadAsync(CoverPayloadReference payload, CancellationToken cancellationToken) => throw new InvalidOperationException("No cover should be read.");
  }

  private sealed class Idle : IConversionService
  {
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null) => throw new InvalidOperationException("No conversion should run.");
  }
}
