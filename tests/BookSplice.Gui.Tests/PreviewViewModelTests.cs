using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Preview;
using BookSplice.Core.Settings;
using BookSplice.Gui;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class PreviewViewModelTests : IDisposable
{
  private const long Second = 1_000_000;
  private static readonly string Root = Path.GetFullPath("preview-book");
  private readonly string _previewRoot = Path.Combine(Path.GetTempPath(), "booksplice-preview-tests-" + Guid.NewGuid().ToString("N"));
  private readonly FakeDecoder _decoder = new();
  private readonly List<FakePlayer> _players = [];

  public void Dispose()
  {
    foreach (var player in _players) player.Dispose();
    try { Directory.Delete(_previewRoot, true); } catch (DirectoryNotFoundException) { }
  }

  private PreviewViewModel Create() => new(_decoder, () => { var player = new FakePlayer(); _players.Add(player); return player; }, _previewRoot);

  private string[] Files() => Directory.Exists(_previewRoot) ? Directory.GetFiles(_previewRoot, "*", SearchOption.AllDirectories) : [];

  // Two tracks of 300 s each.
  private static BookQueueItemViewModel Book(BookAnalysisStatus status = BookAnalysisStatus.Ready, params long[] seconds)
  {
    var item = new BookQueueItemViewModel(Root, AppSettings.Defaults);
    item.SetAnalysis(Analysis(status, seconds));
    return item;
  }

  private static BookAnalysis Analysis(BookAnalysisStatus status, long[] seconds)
  {
    if (seconds.Length == 0) seconds = [300, 300];
    var names = seconds.Select((_, index) => $"{index + 1}.mp3").ToArray();
    long start = 0;
    var entries = seconds.Select((length, index) => { var entry = new ChapterEntry(start, start + length * Second, $"Chapter {index + 1}", $"Chapter {index + 1}", names[index]); start += length * Second; return entry; }).ToArray();
    var metadata = new BookMetadata(new Dictionary<SemanticField, AggregatedValue>
    {
      [SemanticField.BookTitle] = new(SemanticField.BookTitle, AggregationState.Consistent, "Book", []),
    }, new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
    var files = names.Select((name, index) => new SourceFile(Path.Combine(Root, name), name,
      new MediaProbeResult([new AudioTrack(0, "mp3", "audio", 1, 44100, 1m, new Rational(1, 44100), new Dictionary<string, string>())], [], [], new TagCollection([]), new Dictionary<string, string>(), [], seconds[index]),
      new SourceIdentity(Path.Combine(Root, name), 100, DateTime.UnixEpoch, name)));
    return new BookAnalysis(status, Root, "book", files, null, metadata, new CoverDiscoveryResult([], [], null), ChapterPlan.Valid(entries, start), [], [], names);
  }

  [Fact]
  public void OnlyAReadyBookCanPlay()
  {
    var preview = Create();
    preview.SetBook(null);
    Assert.False(preview.CanPlay);
    preview.SetBook(Book(BookAnalysisStatus.InspectOnly));
    Assert.False(preview.CanPlay);
    preview.SetBook(Book(BookAnalysisStatus.NeedsDecision));
    Assert.False(preview.CanPlay);
    preview.SetBook(Book());
    Assert.True(preview.CanPlay);
  }

  [Fact]
  public async Task PlayTrackOpensTheFirstWindowAtTheOffsetAndDecodesTheNextAhead()
  {
    var preview = Create();
    preview.SetBook(Book());
    _decoder.AutoComplete = true;

    await preview.PlayTrackAsync(1, 30 * Second);

    var player = Assert.Single(_players);
    Assert.Equal(["Open", "Play"], player.Calls);
    Assert.True(preview.IsPlaying);
    Assert.False(preview.IsDecoding);
    Assert.False(preview.IsBoundary);
    Assert.Equal(2, _decoder.Calls.Count);
    Assert.Equal(330 * Second, _decoder.Calls[0].Region.BookStartMicroseconds);
    Assert.Equal(120 * Second, _decoder.Calls[0].Region.LengthMicroseconds);
    Assert.Equal(450 * Second, _decoder.Calls[1].Region.BookStartMicroseconds);
    Assert.Equal(_decoder.Calls[0].Path, player.OpenedPath);
    Assert.NotEqual(_decoder.Calls[0].Path, _decoder.Calls[1].Path);
  }

  [Fact]
  public async Task CurrentBookTimeIsTheRegionStartPlusThePositionAcrossATrackBoundary()
  {
    var preview = Create();
    preview.SetBook(Book());
    _decoder.AutoComplete = true;
    await preview.PlayTrackAsync(1, 30 * Second);

    _players[0].Position = TimeSpan.FromSeconds(12);
    preview.Tick();

    Assert.Equal(342 * Second, preview.CurrentBookMicroseconds);
    Assert.Equal("0:00:42 / 0:05:00", preview.PositionText);
  }

  [Fact]
  public async Task EndedSwitchesToTheDecodeAheadWindowWithoutAGapAndDeletesTheOldFile()
  {
    var preview = Create();
    preview.SetBook(Book());
    _decoder.AutoComplete = true;
    await preview.PlayTrackAsync(0);
    var first = _decoder.Calls[0].Path;
    var second = _decoder.Calls[1].Path;
    await preview.AheadTask;

    _players[0].RaiseEnded();

    var player = Assert.Single(_players);
    Assert.Equal(["Open", "Play", "Close", "Open", "Play"], player.Calls);
    Assert.Equal(second, player.OpenedPath);
    Assert.False(File.Exists(first));
    Assert.True(File.Exists(second));
    Assert.False(preview.IsDecoding);
    Assert.True(preview.IsPlaying);
    Assert.Equal(3, _decoder.Calls.Count);
    Assert.Equal(240 * Second, _decoder.Calls[2].Region.BookStartMicroseconds);
    Assert.Equal(60 * Second, _decoder.Calls[2].Region.LengthMicroseconds);
    Assert.Equal(120 * Second, preview.CurrentBookMicroseconds);
  }

  [Fact]
  public async Task EndedBeforeTheDecodeAheadFinishesWaitsThenContinues()
  {
    var preview = Create();
    preview.SetBook(Book());
    var first = _decoder.Expect();
    var play = preview.PlayTrackAsync(0);
    first.Complete();
    await play;
    var ahead = _decoder.Calls[1];
    Assert.False(ahead.IsCompleted);

    _players[0].RaiseEnded();

    Assert.True(preview.IsDecoding);
    Assert.Equal(["Open", "Play"], _players[0].Calls);

    ahead.Complete();

    Assert.False(preview.IsDecoding);
    Assert.Equal(["Open", "Play", "Close", "Open", "Play"], _players[0].Calls);
    Assert.Equal(ahead.Path, _players[0].OpenedPath);
    Assert.True(preview.IsPlaying);
  }

  [Fact]
  public async Task SeekInsideTheCurrentWindowOnlyMovesThePosition()
  {
    var preview = Create();
    preview.SetBook(Book());
    _decoder.AutoComplete = true;
    await preview.PlayTrackAsync(0, 10 * Second);
    var decodes = _decoder.Calls.Count;

    await preview.SeekAsync(70 * Second);

    Assert.Equal(TimeSpan.FromSeconds(60), _players[0].Position);
    Assert.Equal(decodes, _decoder.Calls.Count);
    Assert.Equal(["Open", "Play"], _players[0].Calls);
  }

  [Fact]
  public async Task SeekOutsideTheWindowCancelsTheInFlightDecodeAndDecodesANewWindowThere()
  {
    var preview = Create();
    preview.SetBook(Book());
    var first = _decoder.Expect();
    var play = preview.PlayTrackAsync(0);
    first.Complete();
    await play;
    var ahead = _decoder.Calls[1];
    var aheadFile = ahead.Path;
    var second = _decoder.Expect();

    var seek = preview.SeekAsync(400 * Second);

    Assert.True(ahead.Token.IsCancellationRequested);
    Assert.True(preview.IsDecoding);
    Assert.Equal(400 * Second, second.Region.BookStartMicroseconds);
    second.Complete();
    await seek;
    Assert.False(File.Exists(first.Path));
    Assert.False(File.Exists(aheadFile));
    Assert.True(File.Exists(second.Path));
    Assert.Equal(second.Path, _players.Last().OpenedPath);
    Assert.Equal(400 * Second, preview.CurrentBookMicroseconds);
  }

  [Fact]
  public async Task AStaleDecodeCompletingAfterStopIsDiscardedAndItsFileDeleted()
  {
    var preview = Create();
    preview.SetBook(Book());
    _decoder.HonorCancel = false;
    var first = _decoder.Expect();
    var play = preview.PlayTrackAsync(0);

    preview.Stop();
    first.Complete();
    await play;

    Assert.Empty(_players);
    Assert.False(preview.IsPlaying);
    Assert.False(preview.IsDecoding);
    Assert.Empty(Files());
  }

  [Fact]
  public async Task AStaleDecodeAheadCompletingAfterASeekIsDiscarded()
  {
    var preview = Create();
    _decoder.HonorCancel = false;
    preview.SetBook(Book());
    var first = _decoder.Expect();
    var play = preview.PlayTrackAsync(0);
    first.Complete();
    await play;
    var ahead = _decoder.Calls[1];
    var seek = preview.SeekAsync(500 * Second);
    var fresh = _decoder.Calls[2];

    ahead.Complete();
    await preview.AheadTask;
    fresh.Complete();
    await seek;

    Assert.False(File.Exists(ahead.Path));
    Assert.Single(_players, player => !player.IsDisposed);
    Assert.Equal(fresh.Path, _players.Last().OpenedPath);
  }

  [Fact]
  public async Task ABoundaryPlaysOneRegionAndStopsAtItsEnd()
  {
    var preview = Create();
    preview.SetBook(Book());
    _decoder.AutoComplete = true;

    await preview.PlayBoundaryAsync(300 * Second);

    Assert.True(preview.IsBoundary);
    Assert.True(preview.IsPlaying);
    var call = Assert.Single(_decoder.Calls);
    Assert.Equal(295 * Second, call.Region.BookStartMicroseconds);
    Assert.Equal(10 * Second, call.Region.LengthMicroseconds);
    _players[0].Position = TimeSpan.FromSeconds(3);
    preview.Tick();
    Assert.Equal("0:00:03 / 0:00:10", preview.PositionText);
    Assert.Equal(298 * Second, preview.CurrentBookMicroseconds);

    _players[0].RaiseEnded();

    Assert.False(preview.IsPlaying);
    Assert.False(preview.IsBoundary);
    Assert.True(_players[0].IsDisposed);
    Assert.Single(_decoder.Calls);
    Assert.Empty(Files());
  }

  [Fact]
  public async Task TheEndOfTheLastWindowStops()
  {
    var preview = Create();
    preview.SetBook(Book());
    _decoder.AutoComplete = true;

    await preview.PlayTrackAsync(1, 290 * Second);
    Assert.Single(_decoder.Calls);
    _players[0].RaiseEnded();

    Assert.False(preview.IsPlaying);
    Assert.True(_players[0].IsDisposed);
    Assert.Single(_players);
    Assert.Empty(Files());
  }

  [Fact]
  public async Task ASecondWindowEndingAtTheTrackEndStopsWithoutStartingTheNextTrack()
  {
    var preview = Create();
    preview.SetBook(Book(BookAnalysisStatus.Ready, 150, 300));
    _decoder.AutoComplete = true;
    await preview.PlayTrackAsync(0);
    await preview.AheadTask;
    _players[0].RaiseEnded();
    Assert.True(preview.IsPlaying);
    Assert.Equal(2, _decoder.Calls.Count);

    _players[0].RaiseEnded();

    Assert.False(preview.IsPlaying);
    Assert.Equal(2, _decoder.Calls.Count);
  }

  [Fact]
  public async Task SwitchingBookReleasingAndStoppingCloseThePlayerAndEmptyThePreviewFolder()
  {
    foreach (var action in new Action<PreviewViewModel>[] { preview => preview.SetBook(Book()), preview => preview.SetBook(null), preview => preview.Release(), preview => preview.Stop() })
    {
      _players.Clear();
      var preview = Create();
      preview.SetBook(Book());
      _decoder.AutoComplete = true;
      await preview.PlayTrackAsync(0);
      await preview.AheadTask;
      Assert.NotEmpty(Files());

      action(preview);

      Assert.True(_players[0].IsDisposed);
      Assert.Contains("Close", _players[0].Calls);
      Assert.False(preview.IsPlaying);
      Assert.Empty(Files());
    }
  }

  [Fact]
  public async Task ReleaseForgetsTheBookButStopKeepsIt()
  {
    var preview = Create();
    preview.SetBook(Book());
    preview.Stop();
    Assert.True(preview.CanPlay);
    preview.Release();
    Assert.False(preview.CanPlay);
    await preview.PlayTrackAsync(0);
    Assert.Empty(_decoder.Calls);
  }

  [Fact]
  public async Task AnUnavailableTrackSetsAMessageAndLeavesTheBookAlone()
  {
    var preview = Create();
    var book = Book();
    var convertible = book.CanConvert;
    var status = book.Status;
    preview.SetBook(book);
    _decoder.Failure = new PreviewUnavailableException("boom");

    await preview.PlayTrackAsync(0);

    Assert.Equal("This track can't be previewed.", preview.Message);
    Assert.Equal(convertible, book.CanConvert);
    Assert.Equal(status, book.Status);
    Assert.False(preview.IsPlaying);
    Assert.False(preview.IsDecoding);
    Assert.Empty(_players);
    Assert.Empty(Files());
  }

  [Fact]
  public async Task APlayerFailureSetsTheMessageAndStops()
  {
    var preview = Create();
    preview.SetBook(Book());
    _decoder.AutoComplete = true;
    await preview.PlayTrackAsync(0);

    _players[0].RaiseFailed("device gone");

    Assert.Equal("The preview could not be played.", preview.Message);
    Assert.False(preview.IsPlaying);
    Assert.True(_players[0].IsDisposed);
    Assert.Empty(Files());
  }

  [Fact]
  public async Task AStartedConversionReleasesThePreview()
  {
    var preview = Create();
    var model = new MainWindowViewModel(new QueueTests.Analyzer(), new FinishedConverter(), AppSettings.Defaults, preview: preview);
    await model.AddAsync(Path.GetFullPath("book"));
    await model.ResolveOrderAsync(model.Items[0], OrderCandidateId.Metadata);
    preview.SetBook(Book());
    _decoder.AutoComplete = true;
    await preview.PlayTrackAsync(0);

    await model.ConvertAsync(model.Items[0]);

    Assert.True(_players[0].IsDisposed);
    Assert.False(preview.CanPlay);
    Assert.Empty(Files());
  }

  [Fact]
  public async Task AFinishedConversionBringsPlaybackBackForTheSelectedBook()
  {
    var preview = Create();
    var model = new MainWindowViewModel(new PlayableAnalyzer(), new FinishedConverter(), AppSettings.Defaults, preview: preview);
    await model.AddAsync(Root);
    model.SelectedItem = model.Items[0];
    Assert.True(preview.CanPlay);

    await model.ConvertAsync(model.Items[0]);

    Assert.True(preview.CanPlay);
  }

  [Fact]
  public async Task AFinishedConversionDoesNotInterruptAPreviewThatStartedMeanwhile()
  {
    var preview = Create();
    var gate = new GatedConverter();
    var model = new MainWindowViewModel(new PlayableAnalyzer(), gate, AppSettings.Defaults, preview: preview);
    await model.AddAsync(Root);
    model.SelectedItem = model.Items[0];
    var conversion = model.ConvertAsync(model.Items[0]);
    await gate.Started.Task;
    Assert.False(preview.CanPlay);
    preview.SetBook(Book());
    _decoder.AutoComplete = true;
    await preview.PlayTrackAsync(0);
    var player = Assert.Single(_players);

    gate.Release.SetResult();
    await conversion;

    Assert.False(player.IsDisposed);
    Assert.True(preview.IsPlaying);
  }

  [Fact]
  public async Task TheRangeIsTheTrackForATrackAndTheRegionForABoundary()
  {
    var preview = Create();
    preview.SetBook(Book());
    _decoder.AutoComplete = true;
    Assert.Equal(0, preview.RangeLengthMicroseconds);
    Assert.False(preview.CanStop);

    await preview.PlayTrackAsync(1, 30 * Second);
    _players[0].Position = TimeSpan.FromSeconds(12);
    preview.Tick();

    Assert.True(preview.CanStop);
    Assert.Equal(300 * Second, preview.RangeStartMicroseconds);
    Assert.Equal(300 * Second, preview.RangeLengthMicroseconds);
    Assert.Equal(42 * Second, preview.RangePositionMicroseconds);

    await preview.PlayBoundaryAsync(300 * Second);
    _players[^1].Position = TimeSpan.FromSeconds(7);
    preview.Tick();

    Assert.Equal(295 * Second, preview.RangeStartMicroseconds);
    Assert.Equal(10 * Second, preview.RangeLengthMicroseconds);
    Assert.Equal(7 * Second, preview.RangePositionMicroseconds);
    Assert.Equal(302 * Second, preview.CurrentBookMicroseconds);

    preview.Stop();
    Assert.False(preview.CanStop);
    Assert.Equal(0, preview.RangeLengthMicroseconds);
  }

  [Fact]
  public async Task ClosingTheWindowModelReleasesThePreview()
  {
    var preview = Create();
    var model = new MainWindowViewModel(new QueueTests.Analyzer(), new FinishedConverter(), AppSettings.Defaults, preview: preview);
    preview.SetBook(Book());
    _decoder.AutoComplete = true;
    await preview.PlayTrackAsync(0);

    model.Dispose();

    Assert.True(_players[0].IsDisposed);
    Assert.Empty(Files());
  }

  [Fact]
  public void SweepDeletesWhatAnEarlierRunLeft()
  {
    var folder = Path.Combine(_previewRoot, "old");
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "1.wav"), "x");
    File.WriteAllText(Path.Combine(_previewRoot, "loose.wav"), "x");

    PreviewViewModel.Sweep(_previewRoot);

    Assert.Empty(Files());
    PreviewViewModel.Sweep(Path.Combine(_previewRoot, "missing"));
  }

  private sealed class PlayableAnalyzer : IBookAnalyzer
  {
    public Task<BookAnalysis> AnalyzeAsync(SourceSelection selection, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default) => AnalyzeAsync(selection.RootPath, chaptersEnabled, options, cancellationToken);

    public Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
      => Task.FromResult(Analysis(BookAnalysisStatus.Ready, []));
  }

  private sealed class GatedConverter : IConversionService
  {
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      Started.SetResult();
      await Release.Task;
      return new ConversionServiceResult(Guid.NewGuid(), ConversionTerminalStatus.Succeeded, ConversionStage.Completed, null, null, null, null, null, [], [], "finished.m4b");
    }
  }

  private sealed class FinishedConverter : IConversionService
  {
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
      => Task.FromResult(new ConversionServiceResult(Guid.NewGuid(), ConversionTerminalStatus.Succeeded, ConversionStage.Completed, null, null, null, null, null, [], [], "finished.m4b"));
  }
}
