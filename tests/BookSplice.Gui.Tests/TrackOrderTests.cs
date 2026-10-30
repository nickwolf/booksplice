using System.Windows.Input;
using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class TrackOrderTests
{
  private static readonly string Root = Path.GetFullPath("trackbook");

  private static async Task<(MainWindowViewModel Model, BookQueueItemViewModel Item, Analyzer Analyzer, Converter Converter)> StartAsync(ConversionTerminalStatus status = ConversionTerminalStatus.Succeeded)
  {
    var analyzer = new Analyzer();
    var converter = new Converter(status);
    var model = new MainWindowViewModel(analyzer, converter, AppSettings.Defaults);
    await model.AddAsync(Root);
    return (model, model.Items[0], analyzer, converter);
  }

  [Fact]
  public async Task TracksMoveAndMoveStopsAtTheEnds()
  {
    var (_, item, _, _) = await StartAsync();
    Assert.Equal(["a.mp3", "b.mp3", "c.mp3"], item.IncludedTracks);

    Assert.True(item.MoveTrack("c.mp3", -1));
    Assert.True(item.MoveTrack("a.mp3", 1));
    Assert.True(item.MoveTrack("a.mp3", 1));
    Assert.False(item.MoveTrack("a.mp3", 1));
    Assert.False(item.MoveTrack("c.mp3", -1));
    Assert.False(item.MoveTrack("missing.mp3", 1));

    Assert.Equal(["c.mp3", "b.mp3", "a.mp3"], item.IncludedTracks);
    Assert.True(item.IsTrackListEdited);
  }

  [Fact]
  public async Task ExcludedTracksAreListedSeparatelyAndCanBePutBack()
  {
    var (_, item, _, _) = await StartAsync();

    Assert.True(item.ExcludeTrack("a.mp3"));
    Assert.False(item.ExcludeTrack("a.mp3"));
    Assert.Equal(["b.mp3", "c.mp3"], item.IncludedTracks);
    Assert.Equal(["a.mp3"], item.ExcludedTracks);
    Assert.True(item.IncludeTrack("a.mp3"));

    Assert.Equal(["b.mp3", "c.mp3", "a.mp3"], item.IncludedTracks);
    Assert.Empty(item.ExcludedTracks);
  }

  [Fact]
  public async Task ApplyingReanalyzesWithTheReviewedSequenceAndShowsTheExcludedFiles()
  {
    var (model, item, analyzer, _) = await StartAsync();
    item.MoveTrack("c.mp3", -2);
    item.ExcludeTrack("b.mp3");

    await model.ApplyTrackListAsync(item);

    var reviewed = analyzer.Options[^1]!.ReviewedOrder!;
    Assert.Equal(["c.mp3", "a.mp3"], reviewed.Included);
    Assert.Equal(["b.mp3"], reviewed.Excluded);
    Assert.Equal(["c.mp3", "a.mp3"], item.OrderedFiles);
    Assert.Equal(["b.mp3"], item.ExcludedTracks);
    Assert.False(item.IsTrackListEdited);
    Assert.Contains("1 excluded", item.Summary, StringComparison.Ordinal);
    Assert.True(item.CanConvert);
  }

  [Fact]
  public async Task AnEmptyIncludedListIsNeverSent()
  {
    var (model, item, analyzer, _) = await StartAsync();
    foreach (var id in item.IncludedTracks.ToArray()) item.ExcludeTrack(id);
    var calls = analyzer.Options.Count;

    await model.ApplyTrackListAsync(item);

    Assert.Equal(calls, analyzer.Options.Count);
    Assert.Contains("at least one track", model.Message, StringComparison.OrdinalIgnoreCase);
    Assert.False(item.CanApplyTrackList);
  }

  [Fact]
  public async Task EditsAreRefusedWhileTheBookIsBusy()
  {
    var (model, item, analyzer, _) = await StartAsync();
    item.IsBusy = true;
    var calls = analyzer.Options.Count;

    Assert.False(item.MoveTrack("a.mp3", 1));
    Assert.False(item.ExcludeTrack("a.mp3"));
    await model.ApplyTrackListAsync(item);

    Assert.Equal(calls, analyzer.Options.Count);
    Assert.Equal(["a.mp3", "b.mp3", "c.mp3"], item.IncludedTracks);
  }

  [Fact]
  public async Task ConversionAndRetryCarryTheReviewedSequenceAndOnlyTheIncludedSourcePaths()
  {
    var (model, item, analyzer, converter) = await StartAsync(ConversionTerminalStatus.ExecutionFailed);
    item.MoveTrack("c.mp3", -2);
    item.ExcludeTrack("b.mp3");
    await model.ApplyTrackListAsync(item);

    await model.ConvertAsync(item);
    var first = converter.Requests[^1];
    await model.ConvertAsync(item);
    var retry = converter.Requests[^1];

    Assert.Equal(2, converter.Requests.Count);
    foreach (var request in new[] { first, retry })
    {
      Assert.Equal(["c.mp3", "a.mp3"], request.AnalysisOptions!.ReviewedOrder!.Included);
      Assert.Equal(["b.mp3"], request.AnalysisOptions.ReviewedOrder.Excluded);
      Assert.Equal([Path.Combine(Root, "c.mp3"), Path.Combine(Root, "a.mp3")], request.ExpectedSourcePaths);
      Assert.Equal(2, request.ExpectedSources!.Count);
    }
  }

  [Fact]
  public async Task ChoosingACandidateOrderDropsTheReviewedSequence()
  {
    var (model, item, analyzer, _) = await StartAsync();
    item.ExcludeTrack("a.mp3");
    await model.ApplyTrackListAsync(item);

    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);

    Assert.Null(analyzer.Options[^1]!.ReviewedOrder);
    Assert.Null(item.ReviewedOrder);
    Assert.Equal(["a.mp3", "b.mp3", "c.mp3"], item.IncludedTracks);
  }

  [Fact]
  public async Task UsingAnUneditedListAppliesTheCandidateAndAnEditedListAppliesTheSequence()
  {
    var (model, item, analyzer, _) = await StartAsync();
    await model.UseTrackListAsync(item, OrderCandidateId.Metadata);
    Assert.Null(analyzer.Options[^1]!.ReviewedOrder);
    Assert.Equal(OrderCandidateId.Metadata, analyzer.Options[^1]!.SelectedOrderCandidateId);

    item.MoveTrack("a.mp3", 1);
    await model.UseTrackListAsync(item, OrderCandidateId.Metadata);
    Assert.NotNull(analyzer.Options[^1]!.ReviewedOrder);
  }

  [Fact]
  public async Task ASourceChangeAfterReviewKeepsTheDraftAndBlocksConversion()
  {
    var (model, item, analyzer, converter) = await StartAsync();
    item.ExcludeTrack("a.mp3");
    analyzer.ChangedSources = true;

    await model.ApplyTrackListAsync(item);
    await model.ConvertAsync(item);

    Assert.Equal(BookAnalysisStatus.NeedsDecision, item.Analysis!.Status);
    Assert.False(item.CanConvert);
    Assert.Empty(converter.Requests);
    Assert.Equal(["b.mp3", "c.mp3"], item.IncludedTracks);
    Assert.Equal(["a.mp3"], item.ExcludedTracks);
  }

  [Fact]
  public void KeyboardMapsToTheSameActionsAsTheButtons()
  {
    Assert.Equal(TrackListAction.MoveUp, TrackListKeys.Resolve(Key.System, Key.Up, ModifierKeys.Alt, false));
    Assert.Equal(TrackListAction.MoveDown, TrackListKeys.Resolve(Key.System, Key.Down, ModifierKeys.Alt, false));
    Assert.Equal(TrackListAction.MoveDown, TrackListKeys.Resolve(Key.Down, Key.None, ModifierKeys.Alt, false));
    Assert.Equal(TrackListAction.Exclude, TrackListKeys.Resolve(Key.Delete, Key.None, ModifierKeys.None, false));
    Assert.Equal(TrackListAction.Include, TrackListKeys.Resolve(Key.Enter, Key.None, ModifierKeys.None, true));
    Assert.Equal(TrackListAction.None, TrackListKeys.Resolve(Key.Up, Key.None, ModifierKeys.None, false));
    Assert.Equal(TrackListAction.None, TrackListKeys.Resolve(Key.Delete, Key.None, ModifierKeys.None, true));
  }

  [Fact]
  public async Task KeyboardActionsEditTheListsLikeTheButtons()
  {
    var (_, item, _, _) = await StartAsync();

    Assert.True(item.ApplyTrackAction(TrackListKeys.Resolve(Key.System, Key.Down, ModifierKeys.Alt, false), "a.mp3"));
    Assert.True(item.ApplyTrackAction(TrackListKeys.Resolve(Key.Delete, Key.None, ModifierKeys.None, false), "c.mp3"));
    Assert.True(item.ApplyTrackAction(TrackListKeys.Resolve(Key.Enter, Key.None, ModifierKeys.None, true), "c.mp3"));

    Assert.Equal(["b.mp3", "a.mp3", "c.mp3"], item.IncludedTracks);
  }

  private static SourceFile File(string name) => new(Path.Combine(Root, name), name,
    new MediaProbeResult([new AudioTrack(0, "mp3", "audio", 1, 44100, 1m, new Rational(1, 44100), new Dictionary<string, string>())], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1m),
    new SourceIdentity(Path.Combine(Root, name), 1, DateTime.UnixEpoch, name));

  private sealed class Analyzer : IBookAnalyzer
  {
    public List<BookAnalysisOptions?> Options { get; } = [];
    public bool ChangedSources { get; set; }

    public Task<BookAnalysis> AnalyzeAsync(SourceSelection selection, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default) => AnalyzeAsync(selection.RootPath, chaptersEnabled, options, cancellationToken);

    public Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      Options.Add(options);
      string[] all = ["a.mp3", "b.mp3", "c.mp3"];
      var natural = new OrderCandidate(OrderCandidateId.NaturalPath, all, true, OrderConfidence.High, []);
      var tags = new OrderCandidate(OrderCandidateId.Metadata, all, true, OrderConfidence.High, []);
      var resolution = new OrderResolution(OrderStatus.Resolved, natural, [natural, tags], []);
      var metadata = new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
      var included = options?.ReviewedOrder?.Included ?? all;
      var excluded = options?.ReviewedOrder?.Excluded ?? [];
      var status = ChangedSources && options?.ReviewedOrder is not null ? BookAnalysisStatus.NeedsDecision : BookAnalysisStatus.Ready;
      return Task.FromResult(new BookAnalysis(status, inputPath, "trackbook", status == BookAnalysisStatus.Ready ? included.Select(File) : [], resolution, metadata,
        new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([], 0), [], status == BookAnalysisStatus.Ready ? excluded.Select(File) : []));
    }
  }

  private sealed class Converter(ConversionTerminalStatus status) : IConversionService
  {
    public List<ConversionRequest> Requests { get; } = [];

    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      Requests.Add(request);
      return Task.FromResult(new ConversionServiceResult(Guid.NewGuid(), status, ConversionStage.Completed, null, null, null, null, null, [], [], null));
    }
  }
}
