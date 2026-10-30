using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Queue;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class QueueRestoreTests : IDisposable
{
  private static readonly TimeSpan Never = TimeSpan.FromHours(1);
  private readonly string _directory = Path.Combine(Path.GetTempPath(), "queue-restore-" + Guid.NewGuid().ToString("N"));
  private readonly string _bookA = Path.GetFullPath("restore-a");
  private readonly string _bookB = Path.GetFullPath("restore-b");
  private readonly string _bookC = Path.GetFullPath("restore-c");

  public void Dispose()
  {
    // A background save from an abandoned first session can still be finishing.
    for (var attempt = 0; attempt < 10 && Directory.Exists(_directory); attempt++)
    {
      try { Directory.Delete(_directory, true); }
      catch (IOException) { Thread.Sleep(50); }
    }
  }

  private static async Task<(MainWindowViewModel Model, Converter Converter, Analyzer Analyzer)> StartAsync(MemoryStore store, Disk disk, Converter? converter = null)
  {
    var analyzer = new Analyzer(disk);
    converter ??= new Converter();
    var model = new MainWindowViewModel(analyzer, converter, AppSettings.Defaults);
    await model.AttachQueueAsync(store, Never, action => action());
    return (model, converter, analyzer);
  }

  private static async Task<MainWindowViewModel> StartModelAsync(IQueueSnapshotStore store, Disk disk)
  {
    var model = new MainWindowViewModel(new Analyzer(disk), new Converter(), AppSettings.Defaults);
    await model.AttachQueueAsync(store, Never, action => action());
    return model;
  }

  [Fact]
  public async Task RestoreRebuildsTheQueueWithOrderSettingsAndEveryReviewedChoice()
  {
    var store = new MemoryStore();
    var disk = new Disk();
    var (first, _, _) = await StartAsync(store, disk);
    await first.AddManyAsync([_bookA, _bookB, _bookC]);
    var a = first.Items[0];
    a.QualityProfileId = "efficient";
    a.OutputDirectory = Path.Combine(_directory, "out-a");
    a.ChannelPolicy = BookSplice.Core.Planning.ChannelPolicy.ForceMono;
    a.OutputTemplate = "{Title}";
    a.ValidationLevel = ValidationLevel.Full;
    await first.ResolveOrderAsync(a, OrderCandidateId.Metadata);
    a.MoveTrack("3.mp3", -2);
    a.ExcludeTrack("2.mp3");
    await first.ApplyTrackListAsync(a);
    a.Metadata.Single(field => field.Field == SemanticField.BookTitle).Value = "Edited title";
    a.Metadata.Single(field => field.Field == SemanticField.Author).Value = "";
    a.Chapters[0].Title = "Opening";
    a.SelectedCover = a.Covers[1];
    first.Items[1].OmitCover = true;
    first.Move(first.Items[2], -1);
    first.FlushQueue();

    var (second, converter, analyzer) = await StartAsync(store, disk);

    Assert.Equal([_bookA, _bookC, _bookB], second.Items.Select(item => item.Source));
    var restored = second.Items[0];
    Assert.Equal("efficient", restored.QualityProfileId);
    Assert.Equal(Path.Combine(_directory, "out-a"), restored.OutputDirectory);
    Assert.Equal(BookSplice.Core.Planning.ChannelPolicy.ForceMono, restored.ChannelPolicy);
    Assert.Equal("{Title}", restored.OutputTemplate);
    Assert.Equal(ValidationLevel.Full, restored.ValidationLevel);
    Assert.Equal(["3.mp3", "1.mp3"], restored.ReviewedOrder!.Included);
    Assert.Equal(["2.mp3"], restored.ReviewedOrder.Excluded);
    Assert.Equal(["3.mp3", "1.mp3"], restored.OrderedFiles);
    Assert.Equal("Edited title", restored.Metadata.Single(field => field.Field == SemanticField.BookTitle).Value);
    Assert.Equal("", restored.Metadata.Single(field => field.Field == SemanticField.Author).Value);
    Assert.Equal("Opening", restored.Chapters.Single(chapter => chapter.Source == "3.mp3").Title);
    Assert.Equal("cover.jpg", restored.SelectedCover!.SourcePath.Split(Path.DirectorySeparatorChar)[^1]);
    Assert.True(second.Items[2].OmitCover);
    Assert.True(restored.CanConvert);
    Assert.Null(restored.ReviewReason);
    Assert.Empty(converter.Requests);
    Assert.False(second.IsQueuePaused);
    var request = await ConvertAndGetRequestAsync(second, restored, converter);
    Assert.Equal("Edited title", request.Options.MetadataEdits[SemanticField.BookTitle].Value);
    Assert.Equal(analyzer.LastReviewed(_bookA)!.Included, request.AnalysisOptions!.ReviewedOrder!.Included);
  }

  [Fact]
  public async Task AFileSelectionComesBackWithTheSameFiles()
  {
    var store = new MemoryStore();
    var disk = new Disk();
    var (first, _, _) = await StartAsync(store, disk);
    var files = new[] { Path.Combine(_bookA, "x.mp3"), Path.Combine(_bookA, "sub", "y.mp3") };
    await first.AddFilesAsync(files, _ => FileImportChoice.OneBook);
    first.FlushQueue();

    var (second, _, analyzer) = await StartAsync(store, disk);

    var item = Assert.Single(second.Items);
    Assert.Equal(SourceSelection.Create(files), item.Selection);
    Assert.Single(analyzer.Selections);
  }

  [Fact]
  public async Task AChangedSourceNeedsReviewKeepsTheEditsAndStaysChecked()
  {
    var store = new MemoryStore();
    var disk = new Disk();
    var (first, _, _) = await StartAsync(store, disk);
    await first.AddAsync(_bookA);
    first.Items[0].Metadata.Single(field => field.Field == SemanticField.BookTitle).Value = "Edited title";
    first.FlushQueue();
    disk.Length = 999;

    var (second, converter, _) = await StartAsync(store, disk);
    var item = second.Items[0];

    Assert.Equal("Needs review", item.QueueStatus);
    Assert.Contains("3 changed", item.ReviewReason, StringComparison.Ordinal);
    Assert.False(item.CanConvert);
    Assert.Equal("Edited title", item.Metadata.Single(field => field.Field == SemanticField.BookTitle).Value);
    await second.ConvertAsync(item);
    await second.ConvertAllAsync();
    Assert.Empty(converter.Requests);

    second.FlushQueue();
    Assert.Equal(100, store.Snapshot!.Items[0].ExpectedSources![0].Length);

    var (third, _, _) = await StartAsync(store, disk);
    Assert.False(third.Items[0].CanConvert);

    await third.ApplyTrackListAsync(third.Items[0]);
    Assert.True(third.Items[0].CanConvert);
    Assert.Equal("Edited title", third.Items[0].Metadata.Single(field => field.Field == SemanticField.BookTitle).Value);
    third.FlushQueue();
    Assert.Equal(999, store.Snapshot!.Items[0].ExpectedSources![0].Length);

    var (fourth, _, _) = await StartAsync(store, disk);
    Assert.True(fourth.Items[0].CanConvert);
  }

  [Fact]
  public async Task AFileThatAppearedAfterReviewSendsTheBookBackToTheTrackList()
  {
    var store = new MemoryStore();
    var disk = new Disk();
    var (first, _, _) = await StartAsync(store, disk);
    await first.AddAsync(_bookA);
    first.Items[0].ExcludeTrack("2.mp3");
    await first.ApplyTrackListAsync(first.Items[0]);
    first.Items[0].Chapters[0].Title = "Opening";
    first.FlushQueue();
    disk.Files = ["1.mp3", "2.mp3", "3.mp3", "4.mp3"];

    var (second, converter, _) = await StartAsync(store, disk);
    var item = second.Items[0];

    Assert.False(item.CanConvert);
    Assert.Equal(["1.mp3", "3.mp3"], item.IncludedTracks);
    Assert.Equal(["2.mp3", "4.mp3"], item.ExcludedTracks);
    Assert.NotNull(item.ReviewReason);
    await second.ApplyTrackListAsync(item);
    Assert.True(item.CanConvert);
    Assert.Equal("Opening", item.Chapters.Single(chapter => chapter.Source == "1.mp3").Title);
    Assert.Empty(converter.Requests);
  }

  [Fact]
  public async Task ADeletedSourceKeepsTheBookAndItsEditsUntilTheSourceIsBack()
  {
    var store = new MemoryStore();
    var disk = new Disk();
    var (first, _, _) = await StartAsync(store, disk);
    await first.AddAsync(_bookA);
    first.Items[0].Metadata.Single(field => field.Field == SemanticField.BookTitle).Value = "Edited title";
    first.Items[0].Chapters[1].Title = "Middle";
    first.Items[0].SelectedCover = first.Items[0].Covers[1];
    first.FlushQueue();
    disk.Missing = true;

    var (second, converter, _) = await StartAsync(store, disk);
    var item = second.Items[0];

    Assert.Equal("Analysis failed", item.Status);
    Assert.Equal("Needs review", item.QueueStatus);
    Assert.False(item.CanConvert);
    Assert.True(item.CanApplyTrackList);
    second.FlushQueue();
    var kept = store.Snapshot!.Items[0];
    Assert.Equal("Edited title", kept.Metadata!["BookTitle"]);
    Assert.Equal("Middle", kept.ChapterTitles!["2.mp3"]);
    Assert.NotNull(kept.Cover);
    Assert.Equal(100, kept.ExpectedSources![0].Length);

    disk.Missing = false;
    await second.UseTrackListAsync(item, null);

    Assert.True(item.CanConvert);
    Assert.Equal("Edited title", item.Metadata.Single(field => field.Field == SemanticField.BookTitle).Value);
    Assert.Equal("Middle", item.Chapters[1].Title);
    Assert.Equal("cover.jpg", item.SelectedCover!.SourcePath.Split(Path.DirectorySeparatorChar)[^1]);
    Assert.Empty(converter.Requests);
  }

  [Fact]
  public async Task ACoverThatChangedOnDiskIsNotAppliedAndSaysSo()
  {
    var store = new MemoryStore();
    var disk = new Disk();
    var (first, _, _) = await StartAsync(store, disk);
    await first.AddAsync(_bookA);
    first.Items[0].SelectedCover = first.Items[0].Covers[1];
    first.FlushQueue();
    disk.CoverHash = "different";

    var (second, _, _) = await StartAsync(store, disk);

    var item = second.Items[0];
    Assert.Contains("cover you chose has changed", item.ReviewReason, StringComparison.Ordinal);
    Assert.Equal(item.Covers[0], item.SelectedCover);
    Assert.False(item.CanConvert);
  }

  [Theory]
  [InlineData("metadata")]
  [InlineData("quality")]
  public async Task AMissingProfileBlocksTheBookUntilAChoiceIsMade(string which)
  {
    var store = new MemoryStore();
    var disk = new Disk();
    var (first, _, _) = await StartAsync(store, disk);
    await first.AddAsync(_bookA);
    first.FlushQueue();
    var record = store.Snapshot!.Items[0];
    store.Snapshot = store.Snapshot with
    {
      Items = [record with { Settings = which == "metadata" ? record.Settings with { MetadataProfileId = "GoneProfile" } : record.Settings with { QualityProfileId = "gone-quality" } }],
    };

    var (second, converter, _) = await StartAsync(store, disk);
    var item = second.Items[0];

    Assert.Equal("Choose a profile", item.QueueStatus);
    Assert.Contains(which == "metadata" ? "GoneProfile" : "gone-quality", item.BlockReason, StringComparison.Ordinal);
    Assert.Contains("Choose", item.Details, StringComparison.Ordinal);
    Assert.False(item.CanConvert);
    Assert.Equal(which == "metadata" ? "GoneProfile" : "gone-quality", which == "metadata" ? item.MetadataProfileId : item.QualityProfileId);
    await second.ConvertAsync(item);
    Assert.Empty(converter.Requests);

    if (which == "metadata") item.MetadataProfileId = "GenericMp4";
    else item.QualityProfileId = "balanced";

    Assert.Null(item.BlockReason);
    Assert.True(item.CanConvert);
    Assert.Equal("Ready", item.QueueStatus);
    Assert.DoesNotContain("Choose", item.Details, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ABookThatWasConvertingComesBackInterruptedAndIsNeverStartedByTheRestore()
  {
    var store = new MemoryStore();
    var disk = new Disk();
    var running = new Converter { Hold = true };
    var (first, _, _) = await StartAsync(store, disk, running);
    await first.AddAsync(_bookA);
    var item = first.Items[0];
    var conversion = first.ConvertAsync(item);
    await running.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    first.FlushQueue();
    Assert.Equal(QueueItemState.Interrupted, store.Snapshot!.Items[0].State);

    var (second, converter, _) = await StartAsync(store, disk);
    var restored = second.Items[0];

    Assert.Empty(converter.Requests);
    Assert.Equal("Interrupted", restored.QueueStatus);
    Assert.NotEqual("Complete", restored.Status);
    Assert.Null(restored.Result);
    Assert.Equal(0, restored.Percent);
    Assert.False(restored.IsBusy);
    Assert.Contains("cannot be resumed", restored.Details, StringComparison.Ordinal);
    Assert.True(restored.CanConvert);
    Assert.False(second.IsQueuePaused);
    await second.ConvertAllAsync();
    await second.RetryFailedAsync();
    Assert.Empty(converter.Requests);

    second.FlushQueue();
    Assert.Equal(QueueItemState.Interrupted, store.Snapshot!.Items[0].State);

    await second.ConvertAsync(restored);

    Assert.Single(converter.Requests);
    Assert.Equal("Complete", restored.Status);
    running.Release.TrySetResult();
    await conversion;
  }

  [Fact]
  public async Task AForcedExitMidConversionLeavesAFileThatRestoresAsInterrupted()
  {
    var store = new QueueSnapshotStore(_directory);
    var disk = new Disk();
    var running = new Converter { Hold = true };
    var first = new MainWindowViewModel(new Analyzer(disk), running, AppSettings.Defaults);
    await first.AttachQueueAsync(store, Never, action => action());
    await first.AddAsync(_bookA);
    first.Items[0].Metadata.Single(field => field.Field == SemanticField.BookTitle).Value = "Edited title";
    var conversion = first.ConvertAsync(first.Items[0]);
    await running.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    first.FlushQueue();
    var leftover = Path.Combine(_directory, ".queue.json." + Guid.NewGuid().ToString("N") + ".tmp");
    File.WriteAllText(leftover, "{\"schemaVersion\":1,\"it");

    var second = await StartModelAsync(new QueueSnapshotStore(_directory), disk);

    var item = Assert.Single(second.Items);
    Assert.Equal("Interrupted", item.QueueStatus);
    Assert.Equal("Edited title", item.Metadata.Single(field => field.Field == SemanticField.BookTitle).Value);
    // The first model's own background save can still have a temp file open, so check only the planted one.
    Assert.False(File.Exists(leftover));
    Assert.True(File.Exists(Path.Combine(_directory, QueueSnapshotStore.FileName)));
    running.Release.TrySetResult();
    await conversion;
  }

  [Fact]
  public async Task AFinishedBookKeepsItsResultAndIsNotAnalyzedOrConvertedAgain()
  {
    var store = new MemoryStore();
    var disk = new Disk();
    Directory.CreateDirectory(_directory);
    var published = Path.Combine(_directory, "done.m4b");
    File.WriteAllText(published, "x");
    var (first, _, _) = await StartAsync(store, disk, new Converter { Published = published });
    await first.AddAsync(_bookA);
    await first.ConvertAsync(first.Items[0]);
    first.FlushQueue();
    Assert.Equal(QueueItemState.Succeeded, store.Snapshot!.Items[0].State);

    var (second, converter, analyzer) = await StartAsync(store, disk);
    var item = second.Items[0];

    Assert.Equal("Complete", item.Status);
    Assert.Equal(published, item.PublishedPath);
    Assert.True(item.CanOpenOutput);
    Assert.Contains("Saved to", item.Details, StringComparison.Ordinal);
    Assert.False(item.CanConvert);
    Assert.Empty(analyzer.Calls);
    await second.ConvertAllAsync();
    Assert.Empty(converter.Requests);

    File.Delete(published);
    var (third, _, _) = await StartAsync(store, disk);
    Assert.Equal("Complete", third.Items[0].Status);
    Assert.Contains("no longer at", third.Items[0].Details, StringComparison.Ordinal);
    Assert.False(third.Items[0].CanOpenOutput);
  }

  [Fact]
  public async Task FailedAndCancelledBooksKeepTheirOutcomeAndRetryStillWorks()
  {
    var store = new MemoryStore();
    var disk = new Disk();
    var (first, _, _) = await StartAsync(store, disk, new Converter { Status = ConversionTerminalStatus.ExecutionFailed });
    await first.AddManyAsync([_bookA, _bookB]);
    await first.ConvertAsync(first.Items[0]);
    first.Items[1].MarkWaiting();
    first.Items[1].CancelWaiting();
    first.Items[1].Outcome = BookSplice.Gui.Notifications.BookOutcome.Cancelled;
    first.FlushQueue();
    Assert.Equal([QueueItemState.Failed, QueueItemState.Cancelled], store.Snapshot!.Items.Select(item => item.State));

    var (second, converter, _) = await StartAsync(store, disk);

    Assert.Equal("Conversion failed", second.Items[0].QueueStatus);
    Assert.Equal("Cancelled", second.Items[1].QueueStatus);
    Assert.Contains("failed", second.Items[0].Details, StringComparison.Ordinal);
    await second.RetryFailedAsync();
    Assert.Equal([_bookA], converter.Requests.Select(request => request.Source));
  }

  [Fact]
  public async Task ASaveFailureIsShownAsANoticeAndNeverThrows()
  {
    var store = new MemoryStore { Failure = new IOException("disk full") };
    var disk = new Disk();
    var (model, _, _) = await StartAsync(store, disk);
    await model.AddAsync(_bookA);

    model.FlushQueue();
    model.FlushQueue();

    Assert.Contains("could not be saved", model.QueueNotice, StringComparison.Ordinal);
    Assert.Contains("disk full", model.QueueNotice, StringComparison.Ordinal);
    Assert.Single(model.Items);
    Assert.True(model.Items[0].CanConvert);

    store.Failure = null;
    model.FlushQueue();

    Assert.Equal("", model.QueueNotice);
    Assert.Single(store.Snapshot!.Items);
  }

  [Fact]
  public async Task ChangesAreSavedAfterTheDebounce()
  {
    var store = new MemoryStore();
    var model = new MainWindowViewModel(new Analyzer(new Disk()), new Converter(), AppSettings.Defaults);
    await model.AttachQueueAsync(store, TimeSpan.FromMilliseconds(30), action => action());

    await model.AddManyAsync([_bookA, _bookB]);

    for (var wait = 0; wait < 200 && store.Snapshot?.Items.Count != 2; wait++) await Task.Delay(25);
    Assert.Equal(2, store.Snapshot?.Items.Count);
  }

  [Fact]
  public async Task NothingIsSavedWhileTheQueueIsStillBeingRestored()
  {
    var store = new MemoryStore();
    var disk = new Disk();
    var (first, _, _) = await StartAsync(store, disk);
    await first.AddAsync(_bookA);
    first.Items[0].Metadata.Single(field => field.Field == SemanticField.BookTitle).Value = "Edited title";
    first.FlushQueue();
    var saves = store.Saves;
    disk.Missing = true;

    var (second, _, _) = await StartAsync(store, disk);
    second.FlushQueue();

    Assert.Equal("Edited title", store.Snapshot!.Items[0].Metadata!["BookTitle"]);
    Assert.True(store.Saves > saves);
  }

  [Fact]
  public async Task ACorruptFileIsSetAsideOnceAndTheQueueStartsEmpty()
  {
    Directory.CreateDirectory(_directory);
    var path = Path.Combine(_directory, QueueSnapshotStore.FileName);
    File.WriteAllText(path, "{ truncated");
    var store = new QueueSnapshotStore(_directory);

    var model = await StartModelAsync(store, new Disk());

    Assert.Empty(model.Items);
    Assert.Contains("could not be read", model.QueueNotice, StringComparison.Ordinal);
    Assert.Single(Directory.GetFiles(_directory, "queue.json.corrupt-*"));
    await model.AddAsync(_bookA);
    model.FlushQueue();
    Assert.Single(store.Load().Snapshot!.Items);
  }

  [Fact]
  public async Task ANewerFileIsLeftAloneAndNeverOverwrittenThisSession()
  {
    Directory.CreateDirectory(_directory);
    var path = Path.Combine(_directory, QueueSnapshotStore.FileName);
    const string Future = "{\"schemaVersion\":2,\"items\":[]}";
    File.WriteAllText(path, Future);
    var store = new QueueSnapshotStore(_directory);

    var model = await StartModelAsync(store, new Disk());
    await model.AddAsync(_bookA);
    model.FlushQueue();

    Assert.Empty(model.Items.Skip(1));
    Assert.Contains("newer version", model.QueueNotice, StringComparison.Ordinal);
    Assert.Equal(Future, File.ReadAllText(path));
    Assert.Equal([path], Directory.GetFiles(_directory));
  }

  [Fact]
  public async Task AnUnrestorableBookIsReportedAndTheRestOfTheQueueStillComesBack()
  {
    var store = new MemoryStore();
    var disk = new Disk();
    var (first, _, _) = await StartAsync(store, disk);
    await first.AddManyAsync([_bookA, _bookB]);
    first.FlushQueue();
    var broken = store.Snapshot!.Items[0] with { Selection = new SavedSelection([Path.GetFullPath("elsewhere/x.mp3")], Path.GetFullPath("unrelated"), true) };
    store.Snapshot = store.Snapshot with { Items = [broken, store.Snapshot.Items[1]] };

    var (second, _, _) = await StartAsync(store, disk);

    Assert.Equal([_bookB], second.Items.Select(item => item.Source));
    Assert.Contains("could not be restored", second.Message, StringComparison.Ordinal);
  }

  private static async Task<ConversionRequest> ConvertAndGetRequestAsync(MainWindowViewModel model, BookQueueItemViewModel item, Converter converter)
  {
    await model.ConvertAsync(item);
    return Assert.Single(converter.Requests);
  }

  private sealed class MemoryStore : IQueueSnapshotStore
  {
    public string Path => "memory";
    public QueueSnapshot? Snapshot { get; set; }
    public int Saves { get; private set; }
    public Exception? Failure { get; set; }
    public QueueLoadResult Load() => Snapshot is null ? new(QueueLoadCode.Missing, null) : new(QueueLoadCode.Loaded, Snapshot);

    public bool Save(QueueSnapshot snapshot)
    {
      if (Failure is not null) throw Failure;
      Snapshot = snapshot;
      Saves++;
      return true;
    }
  }

  private sealed class Disk
  {
    public string[] Files { get; set; } = ["1.mp3", "2.mp3", "3.mp3"];
    public long Length { get; set; } = 100;
    public bool Missing { get; set; }
    public string CoverHash { get; set; } = "hash-cover";
  }

  private sealed class Analyzer(Disk disk) : IBookAnalyzer
  {
    private readonly Dictionary<string, ReviewedTrackOrder?> _reviewed = [];
    public List<string> Calls { get; } = [];
    public List<SourceSelection> Selections { get; } = [];
    public ReviewedTrackOrder? LastReviewed(string root) => _reviewed.GetValueOrDefault(root);

    public Task<BookAnalysis> AnalyzeAsync(SourceSelection selection, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      Selections.Add(selection);
      return AnalyzeAsync(selection.RootPath, chaptersEnabled, options, cancellationToken);
    }

    public Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      Calls.Add(inputPath);
      if (disk.Missing) throw new DirectoryNotFoundException("The source folder is not there.");
      var all = disk.Files;
      var reviewed = options?.ReviewedOrder;
      _reviewed[inputPath] = reviewed;
      var natural = new OrderCandidate(OrderCandidateId.NaturalPath, all, true, OrderConfidence.High, []);
      var tags = new OrderCandidate(OrderCandidateId.Metadata, all, true, OrderConfidence.High, []);
      var resolution = new OrderResolution(OrderStatus.Resolved, natural, [natural, tags], []);
      var listed = reviewed is null ? all : reviewed.Included.Concat(reviewed.Excluded).ToArray();
      var matches = listed.Order().SequenceEqual(all.Order());
      var included = matches ? (reviewed?.Included ?? all) : [];
      var chapters = included.Select((id, index) => new ChapterEntry(index * 1000, (index + 1) * 1000, $"Chapter {index + 1}", $"Chapter {index + 1}", id)).ToArray();
      var cover = new CoverCandidate("hash-embedded", CoverOrigin.EmbeddedPicture, Path.Combine(inputPath, "1.mp3"), 0, "image/jpeg", 10, 10, CoverSemanticType.FrontCover, false);
      var external = new CoverCandidate(disk.CoverHash, CoverOrigin.ExternalFile, Path.Combine(inputPath, "cover.jpg"), null, "image/jpeg", 10, 10, CoverSemanticType.FrontCover, true);
      var metadata = new BookMetadata(new Dictionary<SemanticField, AggregatedValue>
      {
        [SemanticField.BookTitle] = new(SemanticField.BookTitle, AggregationState.Consistent, "Book", []),
        [SemanticField.Author] = new(SemanticField.Author, AggregationState.Consistent, "Author", []),
      }, new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
      IEnumerable<AnalysisDiagnostic> diagnostics = matches ? [] : [new("analysis.order-sources-changed", AnalysisDiagnosticSeverity.Warning, "The files in the source changed.")];
      return Task.FromResult(new BookAnalysis(matches ? BookAnalysisStatus.Ready : BookAnalysisStatus.NeedsDecision, inputPath, "book",
        included.Select(id => File(inputPath, id)), resolution, metadata, new CoverDiscoveryResult([cover, external], [], cover),
        ChapterPlan.Valid(chapters, chapters.Length * 1000), diagnostics, matches ? (reviewed?.Excluded ?? []).Select(id => File(inputPath, id)) : [], all));
    }

    private SourceFile File(string root, string name) => new(Path.Combine(root, name), name,
      new MediaProbeResult([new AudioTrack(0, "mp3", "audio", 1, 44100, 1m, new Rational(1, 44100), new Dictionary<string, string>())], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1m),
      new SourceIdentity(Path.Combine(root, name), disk.Length, DateTime.UnixEpoch, name));
  }

  private sealed class Converter : IConversionService
  {
    public List<ConversionRequest> Requests { get; } = [];
    public ConversionTerminalStatus Status { get; init; } = ConversionTerminalStatus.Succeeded;
    public string? Published { get; init; }
    public bool Hold { get; init; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      Requests.Add(request);
      Started.TrySetResult();
      if (Hold) await Release.Task;
      return new ConversionServiceResult(Guid.NewGuid(), Status, ConversionStage.Completed, null, null, null, null, null, [], [], Published);
    }
  }
}
