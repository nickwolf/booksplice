using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;

namespace BookSplice.Core.Analysis;

public enum BookAnalysisStatus { Ready, NeedsDecision, Invalid, InspectOnly }
public enum AnalysisDiagnosticSeverity { Error, Warning, Information }
public sealed record AnalysisDiagnostic(string Code, AnalysisDiagnosticSeverity Severity, string Message);
public sealed record BookAnalysisOptions(OrderCandidateId? SelectedOrderCandidateId = null, string? PriorSelectedCoverHash = null);

public sealed class BookAnalysis
{
  public BookAnalysis(BookAnalysisStatus status, string sourceRoot, IEnumerable<SourceFile> orderedFiles, OrderResolution? orderResolution, BookMetadata bookMetadata, CoverDiscoveryResult cover, ChapterPlan chapters, IEnumerable<AnalysisDiagnostic> diagnostics) : this(status, sourceRoot, SourceName(sourceRoot), orderedFiles, orderResolution, bookMetadata, cover, chapters, diagnostics) { }
  public BookAnalysis(BookAnalysisStatus status, string sourceRootPath, string sourceRootName, IEnumerable<SourceFile> orderedFiles, OrderResolution? orderResolution, BookMetadata bookMetadata, CoverDiscoveryResult cover, ChapterPlan chapters, IEnumerable<AnalysisDiagnostic> diagnostics)
  {
    Status = status;
    SourceRootPath = sourceRootPath;
    SourceRootName = string.IsNullOrWhiteSpace(sourceRootName) ? "Audiobook" : sourceRootName;
    OrderedFiles = Array.AsReadOnly(orderedFiles.Select(CopyFile).ToArray());
    OrderResolution = CopyOrder(orderResolution);
    BookMetadata = CopyMetadata(bookMetadata);
    Cover = new CoverDiscoveryResult(Array.AsReadOnly(cover.Candidates.ToArray()), Array.AsReadOnly(cover.Rejections.ToArray()), cover.Selected);
    Chapters = chapters.IsValid ? ChapterPlan.Valid(chapters.Entries.ToArray(), chapters.TotalDurationMicroseconds) : ChapterPlan.Invalid(chapters.ErrorCode!, chapters.ErrorMessage!);
    Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
  }
  public BookAnalysisStatus Status { get; }
  public string SourceRootPath { get; }
  public string SourceRootName { get; }
  public string SourceRoot => SourceRootName;
  public IReadOnlyList<SourceFile> OrderedFiles { get; }
  public OrderResolution? OrderResolution { get; }
  public BookMetadata BookMetadata { get; }
  public CoverDiscoveryResult Cover { get; }
  public ChapterPlan Chapters { get; }
  public IReadOnlyList<AnalysisDiagnostic> Diagnostics { get; }
  private static BookMetadata CopyMetadata(BookMetadata value) => new(value.Fields.ToDictionary(pair => pair.Key, pair => new AggregatedValue(pair.Value.Field, pair.Value.State, pair.Value.Value, Array.AsReadOnly(pair.Value.Candidates.ToArray()))), new Dictionary<string, string>(value.PreservedTags, StringComparer.OrdinalIgnoreCase), value.InputKeys.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)Array.AsReadOnly(pair.Value.ToArray())));
  private static OrderResolution? CopyOrder(OrderResolution? value)
  {
    if (value is null) return null;
    var candidates = value.Candidates.Select(candidate => new OrderCandidate(candidate.Id, candidate.FileIds.ToArray(), candidate.IsCredible, candidate.Confidence, candidate.Evidence.Select(evidence => new OrderEvidence(evidence.Code, Array.AsReadOnly(evidence.FileIds.ToArray()))))).ToArray();
    var selected = value.SelectedCandidate is null ? null : candidates.Single(candidate => candidate.Id == value.SelectedCandidate.Id);
    return new OrderResolution(value.Status, selected, candidates, value.Evidence.Select(evidence => new OrderEvidence(evidence.Code, Array.AsReadOnly(evidence.FileIds.ToArray()))));
  }
  private static string SourceName(string path) { var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); return string.IsNullOrWhiteSpace(name) || name.EndsWith(':') ? "Audiobook" : name; }
  private static SourceFile CopyFile(SourceFile file) => new(file.FullPath, file.RelativePath, new MediaProbeResult(file.ProbeResult.AudioStreams.Select(track => track with { RawTags = new Dictionary<string, string>(track.RawTags, StringComparer.OrdinalIgnoreCase) }).ToArray(), file.ProbeResult.AttachedPictures.Select(picture => picture with { RawTags = new Dictionary<string, string>(picture.RawTags, StringComparer.OrdinalIgnoreCase) }).ToArray(), file.ProbeResult.Chapters.Select(chapter => chapter with { RawTags = new Dictionary<string, string>(chapter.RawTags, StringComparer.OrdinalIgnoreCase) }).ToArray(), new TagCollection(file.ProbeResult.FormatTags), new Dictionary<string, string>(file.ProbeResult.RawTags, StringComparer.OrdinalIgnoreCase), file.ProbeResult.Warnings.ToArray(), file.ProbeResult.Duration, file.ProbeResult.FormatNames, file.ProbeResult.SourceByteSize));
}
