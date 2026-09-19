using BookSplice.Core.Analysis;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;

namespace BookSplice.Core.Planning;

public interface IStorageSpaceProvider { long? GetAvailableBytes(string destinationDirectory); }
public interface IConversionPlanner { Task<ConversionPlanningResult> CreateAsync(BookAnalysis analysis, ConversionOptions options, CancellationToken cancellationToken); }

public sealed class ConversionPlanner : IConversionPlanner
{
  public const int FilterConcatMaximumFiles = 32;
  public const decimal ContainerAndMetadataOverhead = 1.03m;
  public const decimal SafetyMargin = 1.10m;
  private readonly OutputNamePlanner _outputNames;
  private readonly IStorageSpaceProvider _storage;
  public ConversionPlanner(OutputNamePlanner outputNames, IStorageSpaceProvider storage) => (_outputNames, _storage) = (outputNames, storage);

  public Task<ConversionPlanningResult> CreateAsync(BookAnalysis analysis, ConversionOptions options, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(analysis); ArgumentNullException.ThrowIfNull(options); cancellationToken.ThrowIfCancellationRequested();
    if (analysis.Status != BookAnalysisStatus.Ready) return Task.FromResult(new ConversionPlanningResult(analysis.Status, null, analysis.Diagnostics));
    var diagnostics = new List<AnalysisDiagnostic>();
    var metadata = ApplyEdits(analysis.BookMetadata, options.MetadataEdits);
    var title = metadata.Get(SemanticField.BookTitle).Value;
    if (string.IsNullOrWhiteSpace(title)) title = analysis.SourceRootName;
    if (string.IsNullOrWhiteSpace(options.DestinationDirectory) || !Directory.Exists(options.DestinationDirectory)) return Task.FromResult(new ConversionPlanningResult(BookAnalysisStatus.Invalid, null, [new("planning.destination-unavailable", AnalysisDiagnosticSeverity.Error, "The destination directory is unavailable.")]));
    var output = _outputNames.CreateRequestedPath(options.DestinationDirectory, title, analysis.SourceRootName, options.CollisionPolicy);
    var eligibility = StreamCopyEligibility.Evaluate(analysis.OrderedFiles, options.Settings.ChannelPolicy);
    var strategy = eligibility.IsEligible ? AudioStrategy.AacStreamCopy : analysis.OrderedFiles.Count == 1 ? AudioStrategy.DirectTranscode : analysis.OrderedFiles.Count <= FilterConcatMaximumFiles ? AudioStrategy.FilterConcatTranscode : AudioStrategy.SegmentedTranscode;
    var reasons = eligibility.IsEligible ? Array.Empty<string>() : eligibility.ReasonCodes.Concat(strategy == AudioStrategy.SegmentedTranscode ? ["strategy.filter-concat-file-limit"] : []).ToArray();
    SpaceEstimate space;
    try { space = Estimate(analysis, options.QualityProfile, strategy, _storage.GetAvailableBytes(options.DestinationDirectory)); }
    catch (OverflowException) { return Task.FromResult(new ConversionPlanningResult(BookAnalysisStatus.Invalid, null, [new("space.overflow", AnalysisDiagnosticSeverity.Error, "Space requirements exceed the supported range.")])); }
    catch (SizeEvidenceException) { return Task.FromResult(new ConversionPlanningResult(BookAnalysisStatus.Invalid, null, [new("space.size-evidence-missing", AnalysisDiagnosticSeverity.Error, "Copy size evidence is unavailable or invalid.")])); }
    if (space.AvailableBytes is null) return Task.FromResult(new ConversionPlanningResult(BookAnalysisStatus.Invalid, null, [new("planning.destination-space-unavailable", AnalysisDiagnosticSeverity.Error, "Destination free space could not be read.")]));
    if (space.AvailableBytes < space.TotalRequiredBytes) return Task.FromResult(new ConversionPlanningResult(BookAnalysisStatus.Invalid, null, [new("planning.insufficient-space", AnalysisDiagnosticSeverity.Error, "The destination does not have enough available space.")]));
    var cover = string.IsNullOrWhiteSpace(options.SelectedCoverHash) ? analysis.Cover.Selected : analysis.Cover.Candidates.SingleOrDefault(c => string.Equals(c.ContentHash, options.SelectedCoverHash, StringComparison.OrdinalIgnoreCase));
    if (options.OmitCover) cover = null;
    var chapters = analysis.Chapters.Entries.Select(chapter => options.ChapterTitles.TryGetValue(chapter.SourceRelativePath, out var title)
      ? chapter with { Title = string.IsNullOrWhiteSpace(title) ? chapter.Title : title.Trim() } : chapter).ToArray();
    var jobs = options.Settings.ConversionJobs ?? 6;
    var metadataProfileId = options.Settings.MetadataProfileId;
    if (metadataProfileId is not ("GenericMp4" or "NickMp3tag")) return Task.FromResult(new ConversionPlanningResult(BookAnalysisStatus.Invalid, null, [new("planning.metadata-profile-unknown", AnalysisDiagnosticSeverity.Error, "The selected metadata profile is unavailable.")]));
    var format = SelectAudioFormat(analysis, options.Settings.ChannelPolicy);
    var plan = new ConversionPlan(analysis.OrderedFiles.Select(file => file.FullPath).ToArray(), metadata, cover, chapters, options.QualityProfile, options.Settings.ValidationLevel, options.CollisionPolicy, output, strategy, reasons, space, jobs, options.Settings.ConversionJobs is null ? "benchmark-host-automatic-6" : "explicit-setting", metadataProfileId, format.SampleRate, format.Channels);
    return Task.FromResult(new ConversionPlanningResult(BookAnalysisStatus.Ready, plan, diagnostics));
  }

  private static BookMetadata ApplyEdits(BookMetadata source, IReadOnlyDictionary<SemanticField, MetadataEdit> edits)
  {
    var fields = source.Fields.ToDictionary(pair => pair.Key, pair => pair.Value);
    foreach (var (field, edit) in edits.Where(pair => pair.Value.IsSet)) fields[field] = edit.Value is null ? AggregatedValue.Missing(field) : new AggregatedValue(field, AggregationState.Consistent, edit.Value, []);
    return new BookMetadata(fields, new Dictionary<string, string>(source.PreservedTags, StringComparer.OrdinalIgnoreCase), source.InputKeys.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray()));
  }
  private static (int SampleRate, int Channels) SelectAudioFormat(BookAnalysis analysis, ChannelPolicy channelPolicy)
  {
    var tracks = analysis.OrderedFiles.Select(file => file.ProbeResult.AudioStreams.Count > 0 ? file.ProbeResult.AudioStreams[0] : null).ToArray();
    var rates = tracks.Select(track => track?.SampleRate).ToArray();
    var sampleRate = rates.Length > 0 && rates.All(rate => rate is 32_000 or 44_100 or 48_000) && rates.Distinct().Count() == 1 ? rates[0]!.Value : 44_100;
    var channels = tracks.Select(track => track?.Channels).ToArray();
    var channelCount = channelPolicy switch
    {
      ChannelPolicy.ForceMono => 1,
      ChannelPolicy.ForceStereo => 2,
      _ => channels.Length > 0 && channels.All(channel => channel is 1 or 2) && channels.Distinct().Count() == 1 ? channels[0]!.Value : 2,
    };
    return (sampleRate, channelCount);
  }
  private static SpaceEstimate Estimate(BookAnalysis analysis, QualityProfile profile, AudioStrategy strategy, long? available)
  {
    decimal final = strategy == AudioStrategy.AacStreamCopy
      ? analysis.OrderedFiles.Aggregate(0m, (sum, file) => checked(sum + CopyBytes(file)))
      : checked((decimal)analysis.Chapters.TotalDurationMicroseconds / 1_000_000m * profile.AudioBitrateKbps * 125m);
    var finalBytes = checked((long)decimal.Ceiling(final * ContainerAndMetadataOverhead));
    var temporary = strategy == AudioStrategy.SegmentedTranscode ? checked(finalBytes * 2) : finalBytes;
    var combined = checked(finalBytes + temporary);
    var total = checked((long)decimal.Ceiling(checked((decimal)combined * SafetyMargin)));
    return new SpaceEstimate(finalBytes, temporary, total, available, available is null ? null : checked(available.Value - total));
  }
  private static decimal CopyBytes(Discovery.SourceFile file)
  {
    if (file.ProbeResult.SourceByteSize is { } size) return size > 0 ? size : throw new SizeEvidenceException();
    var stream = file.ProbeResult.AudioStreams.SingleOrDefault();
    if (stream?.BitRate is not > 0 || stream.Duration is not > 0) throw new SizeEvidenceException();
    return checked(stream.BitRate.Value / 8m * stream.Duration.Value);
  }
  private sealed class SizeEvidenceException : Exception;
}
