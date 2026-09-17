using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;

namespace BookSplice.Core.Analysis;

public interface IBookAnalyzer
{
  Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default);
}

public sealed class BookAnalyzer : IBookAnalyzer
{
  private readonly ISourceDiscoverer _discoverer;
  private readonly IOrderResolver _orderResolver;
  private readonly IMetadataAggregator _metadataAggregator;
  private readonly ICoverDiscoverer _coverDiscoverer;
  private readonly IChapterPlanner _chapterPlanner;

  public BookAnalyzer(ISourceDiscoverer discoverer, IOrderResolver orderResolver, IMetadataAggregator metadataAggregator, ICoverDiscoverer coverDiscoverer, IChapterPlanner chapterPlanner)
    => (_discoverer, _orderResolver, _metadataAggregator, _coverDiscoverer, _chapterPlanner) = (discoverer, orderResolver, metadataAggregator, coverDiscoverer, chapterPlanner);

  public async Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
    cancellationToken.ThrowIfCancellationRequested();
    options ??= new();
    var discovery = await _discoverer.DiscoverAsync(inputPath, cancellationToken).ConfigureAwait(false);
    var diagnostics = discovery.Errors.Select(d => new AnalysisDiagnostic(d.Code, AnalysisDiagnosticSeverity.Error, d.Message)).Concat(discovery.Warnings.Select(d => new AnalysisDiagnostic(d.Code, AnalysisDiagnosticSeverity.Warning, d.Message))).ToList();
    var root = SourceRootPath(inputPath, discovery.Files);
    var rootName = SourceRootName(root);
    if (discovery.Files.Count == 0)
    {
      diagnostics.Add(new("analysis.no-supported-files", AnalysisDiagnosticSeverity.Error, "No supported source audio files were discovered."));
      return Empty(BookAnalysisStatus.Invalid, root, rootName, diagnostics);
    }
    var resolution = _orderResolver.Resolve(discovery.Files);
    var candidate = SelectCandidate(resolution, options.SelectedOrderCandidateId);
    if (candidate is null)
    {
      diagnostics.Add(new("analysis.order-decision-required", AnalysisDiagnosticSeverity.Warning, "A credible source order must be selected before conversion."));
      return Empty(BookAnalysisStatus.NeedsDecision, root, rootName, diagnostics, resolution);
    }
    var byId = discovery.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
    if (candidate.FileIds.Any(id => !byId.ContainsKey(id)) || candidate.FileIds.Distinct(StringComparer.Ordinal).Count() != discovery.Files.Count)
    {
      diagnostics.Add(new("analysis.order-invalid", AnalysisDiagnosticSeverity.Error, "The selected source order does not identify every discovered file exactly once."));
      return Empty(BookAnalysisStatus.Invalid, root, rootName, diagnostics, resolution);
    }
    var ordered = candidate.FileIds.Select(id => byId[id]).ToArray();
    var metadata = _metadataAggregator.Aggregate(ordered);
    foreach (var field in metadata.Fields.Values.Where(value => value.State == AggregationState.Conflicting)) diagnostics.Add(new($"metadata.{field.Field}.conflict", AnalysisDiagnosticSeverity.Warning, $"Conflicting values were found for {field.Field}."));
    var cover = await _coverDiscoverer.DiscoverAsync(root, ordered, new CoverDiscoveryOptions(options.PriorSelectedCoverHash), cancellationToken).ConfigureAwait(false);
    foreach (var rejection in cover.Rejections) diagnostics.Add(new(rejection.Code, AnalysisDiagnosticSeverity.Warning, rejection.Message));
    var chapters = _chapterPlanner.Create(ordered, chaptersEnabled);
    if (!chapters.IsValid) diagnostics.Add(new(chapters.ErrorCode ?? "chapters.invalid", AnalysisDiagnosticSeverity.Error, chapters.ErrorMessage ?? "The chapter plan is invalid."));
    var existingM4b = ordered.Length == 1 && string.Equals(Path.GetExtension(ordered[0].FullPath), ".m4b", StringComparison.OrdinalIgnoreCase);
    var status = existingM4b ? BookAnalysisStatus.InspectOnly : diagnostics.Any(d => d.Severity == AnalysisDiagnosticSeverity.Error) ? BookAnalysisStatus.Invalid : BookAnalysisStatus.Ready;
    return new BookAnalysis(status, root, rootName, ordered, resolution, metadata, cover, chapters, diagnostics);
  }

  private static OrderCandidate? SelectCandidate(OrderResolution resolution, OrderCandidateId? requested)
    => requested is { } id ? resolution.Candidates.SingleOrDefault(candidate => candidate.Id == id && candidate.IsCredible) : resolution.Status == OrderStatus.Resolved ? resolution.SelectedCandidate : null;
  private static BookAnalysis Empty(BookAnalysisStatus status, string root, string name, IEnumerable<AnalysisDiagnostic> diagnostics, OrderResolution? resolution = null) => new(status, root, name, [], resolution, new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()), new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([], 0), diagnostics);
  private static string SourceRootPath(string inputPath, IReadOnlyList<SourceFile> files)
  {
    var candidate = Directory.Exists(inputPath) ? inputPath : Path.GetDirectoryName(files.Count == 0 ? inputPath : files[0].FullPath);
    return Path.GetFullPath(string.IsNullOrWhiteSpace(candidate) ? inputPath : candidate);
  }
  private static string SourceRootName(string path) { var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); return string.IsNullOrWhiteSpace(name) || name.EndsWith(':') ? "Audiobook" : name; }
}
