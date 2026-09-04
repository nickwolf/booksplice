using AudiobookConverter.Core.Analysis;
using AudiobookConverter.Core.Chapters;
using AudiobookConverter.Core.Covers;
using AudiobookConverter.Core.Metadata;
using AudiobookConverter.Core.Naming;
using AudiobookConverter.Core.Settings;

namespace AudiobookConverter.Core.Planning;

public sealed record SpaceEstimate(long FinalBytes, long TemporaryBytes, long TotalRequiredBytes, long? AvailableBytes, long? MarginBytes);
public sealed class ConversionPlan
{
  public ConversionPlan(IReadOnlyList<string> sourcePaths, BookMetadata metadata, CoverCandidate? cover, IReadOnlyList<ChapterEntry> chapters, QualityProfile qualityProfile, ValidationLevel validationLevel, CollisionPolicy collisionPolicy, string outputPath, AudioStrategy strategy, IEnumerable<string> strategyReasonCodes, SpaceEstimate space, int conversionJobs, string jobsReason, string metadataProfileId, int sampleRate = 44100, int channels = 2, Guid? planId = null)
  {
    SourcePaths = Array.AsReadOnly(sourcePaths.ToArray()); Metadata = metadata; Cover = cover; Chapters = Array.AsReadOnly(chapters.ToArray()); QualityProfile = qualityProfile; ValidationLevel = validationLevel; CollisionPolicy = collisionPolicy; OutputPath = outputPath; Strategy = strategy; StrategyReasonCodes = Array.AsReadOnly(strategyReasonCodes.ToArray()); Space = space; ConversionJobs = conversionJobs; JobsReason = jobsReason; MetadataProfileId = metadataProfileId; SampleRate = sampleRate; Channels = channels; PlanId = planId ?? Guid.NewGuid();
  }
  public IReadOnlyList<string> SourcePaths { get; }
  public BookMetadata Metadata { get; }
  public CoverCandidate? Cover { get; }
  public IReadOnlyList<ChapterEntry> Chapters { get; }
  public QualityProfile QualityProfile { get; }
  public ValidationLevel ValidationLevel { get; }
  public CollisionPolicy CollisionPolicy { get; }
  public string OutputPath { get; }
  public AudioStrategy Strategy { get; }
  public IReadOnlyList<string> StrategyReasonCodes { get; }
  public SpaceEstimate Space { get; }
  public int ConversionJobs { get; }
  public string JobsReason { get; }
  public string MetadataProfileId { get; }
  public Guid PlanId { get; }
  public int SampleRate { get; }
  public int Channels { get; }
  public string ChannelLayout => Channels == 1 ? "mono" : "stereo";
}

public sealed record ConversionPlanningResult(BookAnalysisStatus Status, ConversionPlan? Plan, IReadOnlyList<AnalysisDiagnostic> Diagnostics);
