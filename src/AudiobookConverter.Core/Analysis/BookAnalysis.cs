using AudiobookConverter.Core.Chapters;
using AudiobookConverter.Core.Covers;
using AudiobookConverter.Core.Discovery;
using AudiobookConverter.Core.Metadata;
using AudiobookConverter.Core.Ordering;

namespace AudiobookConverter.Core.Analysis;

public enum BookAnalysisStatus { Ready, NeedsDecision, Invalid, InspectOnly }
public enum AnalysisDiagnosticSeverity { Error, Warning, Information }
public sealed record AnalysisDiagnostic(string Code, AnalysisDiagnosticSeverity Severity, string Message);
public sealed record BookAnalysisOptions(OrderCandidateId? SelectedOrderCandidateId = null, string? PriorSelectedCoverHash = null);

public sealed class BookAnalysis
{
  public BookAnalysis(BookAnalysisStatus status, string sourceRoot, IEnumerable<SourceFile> orderedFiles, OrderResolution? orderResolution, BookMetadata bookMetadata, CoverDiscoveryResult cover, ChapterPlan chapters, IEnumerable<AnalysisDiagnostic> diagnostics)
  {
    Status = status;
    SourceRoot = sourceRoot;
    OrderedFiles = Array.AsReadOnly(orderedFiles.ToArray());
    OrderResolution = orderResolution;
    BookMetadata = bookMetadata;
    Cover = cover;
    Chapters = chapters;
    Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
  }
  public BookAnalysisStatus Status { get; }
  public string SourceRoot { get; }
  public IReadOnlyList<SourceFile> OrderedFiles { get; }
  public OrderResolution? OrderResolution { get; }
  public BookMetadata BookMetadata { get; }
  public CoverDiscoveryResult Cover { get; }
  public ChapterPlan Chapters { get; }
  public IReadOnlyList<AnalysisDiagnostic> Diagnostics { get; }
}
