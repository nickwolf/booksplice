using AudiobookConverter.Core.Analysis;
using AudiobookConverter.Core.Chapters;
using AudiobookConverter.Core.Covers;
using AudiobookConverter.Core.Discovery;
using AudiobookConverter.Core.Metadata;
using AudiobookConverter.Core.Ordering;

namespace AudiobookConverter.Core.Tests.Analysis;

public sealed class BookAnalyzerTests
{
  [Fact]
  public async Task AnalyzeAsync_orchestrates_once_and_applies_the_selected_order()
  {
    var second = File("02.mp3"); var first = File("01.mp3");
    var candidate = Candidate(OrderCandidateId.Metadata, [first.RelativePath, second.RelativePath]);
    var discoverer = new Discoverer(new DiscoveryResult([second, first], [], []));
    var metadata = new Metadata(); var cover = new Covers(); var chapters = new Chapters();
    var analysis = await new BookAnalyzer(discoverer, new Orders(new(OrderStatus.Resolved, candidate, [candidate], [])), metadata, cover, chapters).AnalyzeAsync("book", true, new BookAnalysisOptions(PriorSelectedCoverHash: "old"));

    Assert.Equal(BookAnalysisStatus.Ready, analysis.Status);
    Assert.Equal(["01.mp3", "02.mp3"], analysis.OrderedFiles.Select(file => file.RelativePath));
    Assert.Equal(1, discoverer.Calls); Assert.Equal(1, metadata.Calls); Assert.Equal(1, cover.Calls); Assert.Equal(1, chapters.Calls);
    Assert.Equal("old", cover.PriorHash);
  }

  [Theory]
  [InlineData(true, false)]
  [InlineData(false, true)]
  public async Task AnalyzeAsync_rejects_discovery_errors_and_empty_sources(bool error, bool empty)
  {
    var result = new DiscoveryResult(empty ? [] : [File("01.mp3")], error ? [new DiscoveryDiagnostic("discovery.probe-failed", "bad")] : [], []);
    var analysis = await Analyzer(result).AnalyzeAsync("book", true);
    Assert.Equal(BookAnalysisStatus.Invalid, analysis.Status);
  }

  [Fact]
  public async Task AnalyzeAsync_requires_a_credible_order_choice_and_accepts_an_explicit_credible_choice()
  {
    var files = new[] { File("01.mp3"), File("02.mp3") };
    var natural = Candidate(OrderCandidateId.NaturalPath, files.Select(file => file.RelativePath));
    var metadata = Candidate(OrderCandidateId.Metadata, files.Reverse().Select(file => file.RelativePath));
    var resolution = new OrderResolution(OrderStatus.NeedsDecision, null, [natural, metadata], []);
    var analyzer = Analyzer(new DiscoveryResult(files, [], []), resolution);
    Assert.Equal(BookAnalysisStatus.NeedsDecision, (await analyzer.AnalyzeAsync("book", true)).Status);
    Assert.Equal(BookAnalysisStatus.Ready, (await analyzer.AnalyzeAsync("book", true, new BookAnalysisOptions(OrderCandidateId.Metadata))).Status);
    Assert.Equal(BookAnalysisStatus.NeedsDecision, (await analyzer.AnalyzeAsync("book", true, new BookAnalysisOptions((OrderCandidateId)99))).Status);
  }

  [Fact]
  public async Task AnalyzeAsync_keeps_metadata_and_cover_problems_nonblocking_but_rejects_bad_chapters()
  {
    var metadata = new Metadata(conflict: true); var covers = new Covers(new CoverDiscoveryResult([], [new CoverRejection("cover.rejected", "bad", "cover")], null));
    var ready = await Analyzer(new DiscoveryResult([File("01.mp3")], [], []), metadata: metadata, covers: covers).AnalyzeAsync("book", false);
    Assert.Equal(BookAnalysisStatus.Ready, ready.Status); Assert.Contains(ready.Diagnostics, d => d.Code == "metadata.Author.conflict"); Assert.Contains(ready.Diagnostics, d => d.Code == "cover.rejected");
    var invalid = await Analyzer(new DiscoveryResult([File("01.mp3")], [], []), chapters: new Chapters(ChapterPlan.Invalid("duration.missing", "bad"))).AnalyzeAsync("book", true);
    Assert.Equal(BookAnalysisStatus.Invalid, invalid.Status);
  }

  [Fact]
  public async Task AnalyzeAsync_preserves_cancellation_and_marks_single_m4b_inspect_only()
  {
    var cancelled = new CancellationToken(true);
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Analyzer(new DiscoveryResult([File("01.mp3")], [], [])).AnalyzeAsync("book", true, cancellationToken: cancelled));
    Assert.Equal(BookAnalysisStatus.InspectOnly, (await Analyzer(new DiscoveryResult([File("01.m4b")], [], [])).AnalyzeAsync("book", true)).Status);
  }

  private static BookAnalyzer Analyzer(DiscoveryResult result, OrderResolution? resolution = null, Metadata? metadata = null, Covers? covers = null, Chapters? chapters = null)
  {
    var candidate = Candidate(OrderCandidateId.NaturalPath, result.Files.Select(file => file.RelativePath));
    return new BookAnalyzer(new Discoverer(result), new Orders(resolution ?? new(OrderStatus.Resolved, candidate, [candidate], [])), metadata ?? new(), covers ?? new(), chapters ?? new());
  }
  private static SourceFile File(string path) => new(path, path, new MediaProbeResult([new AudioTrack(0, "mp3", "audio", 1, 44100, 1m, new Rational(1, 44100), new Dictionary<string, string>())], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1m));
  private static OrderCandidate Candidate(OrderCandidateId id, IEnumerable<string> paths) => new(id, paths, true, OrderConfidence.High, []);
  private sealed class Discoverer(DiscoveryResult result) : ISourceDiscoverer { public int Calls { get; private set; } public Task<DiscoveryResult> DiscoverAsync(string path, CancellationToken token = default) { Calls++; return Task.FromResult(result); } }
  private sealed class Orders(OrderResolution result) : IOrderResolver { public OrderResolution Resolve(IReadOnlyList<SourceFile> files) => result; }
  private sealed class Metadata(bool conflict = false) : IMetadataAggregator { public int Calls { get; private set; } public BookMetadata Aggregate(IReadOnlyList<SourceFile> files) { Calls++; var state = conflict ? AggregationState.Conflicting : AggregationState.Consistent; return new BookMetadata(new Dictionary<SemanticField, AggregatedValue> { [SemanticField.BookTitle] = new(SemanticField.BookTitle, AggregationState.Consistent, "Title", []), [SemanticField.Author] = new(SemanticField.Author, state, "Author", []) }, new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()); } }
  private sealed class Covers(CoverDiscoveryResult? result = null) : ICoverDiscoverer { public int Calls { get; private set; } public string? PriorHash { get; private set; } public Task<CoverDiscoveryResult> DiscoverAsync(string root, IReadOnlyList<SourceFile> files, CancellationToken token) => DiscoverAsync(root, files, new(), token); public Task<CoverDiscoveryResult> DiscoverAsync(string root, IReadOnlyList<SourceFile> files, CoverDiscoveryOptions options, CancellationToken token) { Calls++; PriorHash = options.PriorSelectedContentHash; return Task.FromResult(result ?? new CoverDiscoveryResult([], [], null)); } }
  private sealed class Chapters(ChapterPlan? plan = null) : IChapterPlanner { public int Calls { get; private set; } public ChapterPlan Create(IReadOnlyList<SourceFile> files, bool enabled) { Calls++; return plan ?? ChapterPlan.Valid([], enabled ? 1_000_000 : 0); } }
}
