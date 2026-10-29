using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;

namespace BookSplice.Core.Tests.Planning;

public sealed class ConversionPlannerTests
{
  [Theory]
  [InlineData(BookAnalysisStatus.NeedsDecision)]
  [InlineData(BookAnalysisStatus.Invalid)]
  [InlineData(BookAnalysisStatus.InspectOnly)]
  public async Task CreateAsync_rejects_non_ready_analysis(BookAnalysisStatus status)
    => Assert.Null((await Planner().CreateAsync(Analysis(status), Options(), CancellationToken.None)).Plan);

  [Theory]
  [InlineData(1, true, AudioStrategy.AacStreamCopy)]
  [InlineData(1, false, AudioStrategy.DirectTranscode)]
  [InlineData(2, false, AudioStrategy.FilterConcatTranscode)]
  [InlineData(32, false, AudioStrategy.FilterConcatTranscode)]
  [InlineData(33, false, AudioStrategy.SegmentedTranscode)]
  public async Task CreateAsync_selects_the_documented_strategy(int count, bool copy, AudioStrategy expected)
  {
    var result = await Planner().CreateAsync(Analysis(BookAnalysisStatus.Ready, count, copy), Options(), CancellationToken.None);
    Assert.Equal(expected, result.Plan!.Strategy);
    if (!copy) Assert.NotEmpty(result.Plan.StrategyReasonCodes);
  }

  [Fact]
  public async Task CreateAsync_freezes_edits_cover_chapters_profile_and_jobs_without_mutating_analysis()
  {
    var cover = new CoverCandidate("hash", CoverOrigin.ExternalFile, "cover.jpg", null, "image/jpeg", 1, 1, CoverSemanticType.FrontCover, true);
    var analysis = Analysis(BookAnalysisStatus.Ready, cover: cover);
    var edits = new Dictionary<SemanticField, MetadataEdit> { [SemanticField.BookTitle] = MetadataEdit.Set("Edited"), [SemanticField.Author] = MetadataEdit.Clear() };
    var options = Options(edits, jobs: 3, cover: "hash");
    var result = await Planner().CreateAsync(analysis, options, CancellationToken.None);
    Assert.Equal("Edited", result.Plan!.Metadata.Get(SemanticField.BookTitle).Value); Assert.Equal(AggregationState.Missing, result.Plan.Metadata.Get(SemanticField.Author).State); Assert.Equal("Title", analysis.BookMetadata.Get(SemanticField.BookTitle).Value); Assert.Equal(3, result.Plan.ConversionJobs); Assert.Equal("explicit-setting", result.Plan.JobsReason); Assert.Equal("hash", result.Plan.Cover!.ContentHash); Assert.Equal("GenericMp4", result.Plan.MetadataProfileId); Assert.NotSame(analysis.Chapters.Entries, result.Plan.Chapters);
  }

  [Fact]
  public async Task CreateAsync_resolves_the_output_template_from_edited_metadata_and_reports_notes()
  {
    var destination = Directory.CreateTempSubdirectory("booksplice-plan-").FullName;
    try
    {
      var settings = AppSettings.Defaults with { OutputDirectory = destination, OutputTemplate = @"{author}\{series}\{seriesPosition} {title}" };
      var edits = new Dictionary<SemanticField, MetadataEdit> { [SemanticField.Series] = MetadataEdit.Set("Saga"), [SemanticField.SeriesPart] = MetadataEdit.Set("3") };
      var full = await Planner().CreateAsync(Analysis(BookAnalysisStatus.Ready), new ConversionOptions(settings, QualityProfileCatalog.Version1[0], edits), CancellationToken.None);
      Assert.Equal(Path.Combine(destination, "Author", "Saga", "3 Title.m4b"), full.Plan!.OutputPath);
      Assert.Empty(full.Diagnostics);
      Assert.False(Directory.Exists(Path.Combine(destination, "Author")));
      var bare = await Planner().CreateAsync(Analysis(BookAnalysisStatus.Ready), new ConversionOptions(settings, QualityProfileCatalog.Version1[0]), CancellationToken.None);
      Assert.Equal(Path.Combine(destination, "Author", "Title.m4b"), bare.Plan!.OutputPath);
      Assert.Contains(bare.Diagnostics, d => d.Code == "output.folder-skipped");
      Assert.Contains(bare.Diagnostics, d => d.Code == "output.field-missing");
    }
    finally { Directory.Delete(destination, true); }
  }

  [Fact]
  public async Task CreateAsync_refuses_an_invalid_output_template()
  {
    var settings = AppSettings.Defaults with { OutputDirectory = Path.GetTempPath(), OutputTemplate = @"..\{title}" };
    var result = await Planner().CreateAsync(Analysis(BookAnalysisStatus.Ready), new ConversionOptions(settings, QualityProfileCatalog.Version1[0]), CancellationToken.None);
    Assert.Null(result.Plan);
    Assert.Contains(result.Diagnostics, d => d.Code == "planning.output-template-invalid");
  }

  [Fact]
  public async Task CreateAsync_reports_space_failures_and_automatic_jobs()
  {
    var unavailable = await Planner(null).CreateAsync(Analysis(BookAnalysisStatus.Ready), Options(), CancellationToken.None);
    Assert.Contains(unavailable.Diagnostics, d => d.Code == "planning.destination-space-unavailable");
    var insufficient = await Planner(1).CreateAsync(Analysis(BookAnalysisStatus.Ready), Options(), CancellationToken.None);
    Assert.Contains(insufficient.Diagnostics, d => d.Code == "planning.insufficient-space");
    var ready = await Planner(long.MaxValue).CreateAsync(Analysis(BookAnalysisStatus.Ready), Options(), CancellationToken.None);
    Assert.Equal(6, ready.Plan!.ConversionJobs); Assert.Equal("benchmark-host-automatic-6", ready.Plan.JobsReason); Assert.True(ready.Plan.Space.TotalRequiredBytes > ready.Plan.Space.FinalBytes);
  }

  [Fact]
  public async Task CreateAsync_freezes_common_audio_format_and_uses_stereo_fallback_for_mixed_sources()
  {
    var common = await Planner().CreateAsync(AnalysisWithFormats((48_000, 1), (48_000, 1)), Options(), CancellationToken.None);
    Assert.Equal(48_000, common.Plan!.SampleRate);
    Assert.Equal(1, common.Plan.Channels);
    Assert.Equal("mono", common.Plan.ChannelLayout);

    var mixed = await Planner().CreateAsync(AnalysisWithFormats((48_000, 1), (44_100, 2)), Options(), CancellationToken.None);
    Assert.Equal(44_100, mixed.Plan!.SampleRate);
    Assert.Equal(2, mixed.Plan.Channels);
    Assert.Equal("stereo", mixed.Plan.ChannelLayout);
  }

  [Theory]
  [InlineData(ChannelPolicy.ForceMono, 1)]
  [InlineData(ChannelPolicy.ForceStereo, 2)]
  public async Task CreateAsync_forces_requested_channels_and_disables_stream_copy(ChannelPolicy policy, int expectedChannels)
  {
    var settings = AppSettings.Defaults with { OutputDirectory = Path.GetTempPath(), ChannelPolicy = policy };
    var options = new ConversionOptions(settings, QualityProfileCatalog.Version1[0]);

    var result = await Planner().CreateAsync(Analysis(BookAnalysisStatus.Ready, copy: true), options, CancellationToken.None);

    Assert.Equal(AudioStrategy.DirectTranscode, result.Plan!.Strategy);
    Assert.Contains("stream-copy.channel-policy", result.Plan.StrategyReasonCodes);
    Assert.Equal(expectedChannels, result.Plan.Channels);
  }
  [Fact]
  public async Task CreateAsync_preserves_cancellation_and_is_deterministic()
  {
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Planner().CreateAsync(Analysis(BookAnalysisStatus.Ready), Options(), new CancellationToken(true)));
    var planner = Planner(); var first = await planner.CreateAsync(Analysis(BookAnalysisStatus.Ready), Options(), CancellationToken.None); var second = await planner.CreateAsync(Analysis(BookAnalysisStatus.Ready), Options(), CancellationToken.None);
    Assert.Equal(first.Plan!.OutputPath, second.Plan!.OutputPath); Assert.Equal(first.Plan.Space, second.Plan.Space);
  }

  [Fact]
  public async Task CreateAsync_uses_source_root_when_title_is_cleared()
  {
    var analysis = Analysis(BookAnalysisStatus.Ready);
    var result = await Planner().CreateAsync(analysis, Options(new Dictionary<SemanticField, MetadataEdit> { [SemanticField.BookTitle] = MetadataEdit.Clear() }), CancellationToken.None);
    Assert.EndsWith("book.m4b", result.Plan!.OutputPath, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task CreateAsync_snapshots_a_custom_profile_from_the_catalog_into_the_plan()
  {
    using var directory = new TempProfiles();
    directory.Write("synthetic.json", """{"schemaVersion":1,"name":"SyntheticProfile","version":"7","mappings":[{"field":"BookTitle","keys":["TITLE"]}]}""");
    var catalog = MetadataProfileCatalog.Load(directory.Root);
    var options = new ConversionOptions(AppSettings.Defaults with { OutputDirectory = Path.GetTempPath(), MetadataProfileId = "SyntheticProfile" }, QualityProfileCatalog.Version1[0]);

    var result = await new ConversionPlanner(new OutputNamePlanner(), new Storage(long.MaxValue), catalog).CreateAsync(Analysis(BookAnalysisStatus.Ready), options, CancellationToken.None);

    Assert.Same(catalog.Find("SyntheticProfile"), result.Plan!.MetadataProfile);
    Assert.Equal("SyntheticProfile", result.Plan.MetadataProfileId);
    Assert.Equal("7", result.Plan.MetadataProfile.Version);
  }

  [Fact]
  public async Task CreateAsync_reports_an_unknown_metadata_profile()
  {
    var options = new ConversionOptions(AppSettings.Defaults with { OutputDirectory = Path.GetTempPath(), MetadataProfileId = "Missing" }, QualityProfileCatalog.Version1[0]);

    var result = await Planner().CreateAsync(Analysis(BookAnalysisStatus.Ready), options, CancellationToken.None);

    Assert.Null(result.Plan);
    Assert.Contains(result.Diagnostics, d => d.Code == "planning.metadata-profile-unknown");
  }

  [Fact]
  public async Task ChapterDisabledPlanRetainsOrderedDurations()
  {
    var result = await Planner().CreateAsync(AnalysisWithDurations(3m, 1.5m, 7m), Options(), CancellationToken.None);

    Assert.Equal(BookAnalysisStatus.Ready, result.Status);
    Assert.Empty(result.Plan!.Chapters);
    Assert.Equal(new long?[] { 3_000_000, 1_500_000, 7_000_000 }, result.Plan.SourceDurationsMicroseconds);
    Assert.Equal(11_500_000, SourceDurationEvidence.TotalMicroseconds(result.Plan.SourceDurationsMicroseconds));
  }

  [Fact]
  public async Task PlanCopiesDurationEvidence()
  {
    var template = (await Planner().CreateAsync(Analysis(BookAnalysisStatus.Ready), Options(), CancellationToken.None)).Plan!;
    var durations = new long?[] { 2_000_000 };
    var plan = new ConversionPlan(template.SourcePaths, template.Metadata, template.Cover, template.Chapters, template.QualityProfile, template.ValidationLevel, template.CollisionPolicy, template.OutputPath, template.Strategy, template.StrategyReasonCodes, template.Space, template.ConversionJobs, template.JobsReason, template.MetadataProfile, template.SampleRate, template.Channels, template.PlanId, durations);
    durations[0] = 9_000_000;

    Assert.Equal(new long?[] { 2_000_000 }, plan.SourceDurationsMicroseconds);
    Assert.Equal(new long?[] { null }, new ConversionPlan(template.SourcePaths, template.Metadata, template.Cover, template.Chapters, template.QualityProfile, template.ValidationLevel, template.CollisionPolicy, template.OutputPath, template.Strategy, template.StrategyReasonCodes, template.Space, template.ConversionJobs, template.JobsReason, template.MetadataProfile).SourceDurationsMicroseconds);
    Assert.Equal(new long?[] { null }, new ConversionPlan(template.SourcePaths, template.Metadata, template.Cover, template.Chapters, template.QualityProfile, template.ValidationLevel, template.CollisionPolicy, template.OutputPath, template.Strategy, template.StrategyReasonCodes, template.Space, template.ConversionJobs, template.JobsReason, template.MetadataProfile, sourceDurationsMicroseconds: [1, 2]).SourceDurationsMicroseconds);
  }

  [Fact]
  public async Task InvalidDurationDoesNotRejectSupportedPlan()
  {
    var result = await Planner().CreateAsync(AnalysisWithDurations(0m, null, decimal.MaxValue, 0.0000004m, 5_000_000_000_000m, 5_000_000_000_000m), Options(), CancellationToken.None);

    Assert.Equal(BookAnalysisStatus.Ready, result.Status);
    Assert.NotNull(result.Plan);
    Assert.Equal(new long?[] { null, null, null, null, 5_000_000_000_000_000_000, 5_000_000_000_000_000_000 }, result.Plan.SourceDurationsMicroseconds);
    Assert.Null(SourceDurationEvidence.TotalMicroseconds(result.Plan.SourceDurationsMicroseconds));
    Assert.Null(SourceDurationEvidence.TotalMicroseconds([5_000_000_000_000_000_000, 5_000_000_000_000_000_000]));
  }

  [Fact]
  public void SourceDurationEvidenceUsesOnlyUnambiguousAudioFallback()
  {
    var file = File("01.mp3", false);
    var stream = file.ProbeResult.AudioStreams[0] with { Duration = 2.5m };
    var missingContainer = file with { ProbeResult = file.ProbeResult with { Duration = null, AudioStreams = [stream] } };
    var zeroContainer = file with { ProbeResult = file.ProbeResult with { Duration = 0m, AudioStreams = [stream] } };
    var ambiguous = file with { ProbeResult = file.ProbeResult with { Duration = null, AudioStreams = [stream, stream with { Index = 1 }] } };

    Assert.Equal(2_500_000, SourceDurationEvidence.ReadMicroseconds(missingContainer));
    Assert.Equal(2_500_000, SourceDurationEvidence.ReadMicroseconds(zeroContainer));
    Assert.Null(SourceDurationEvidence.ReadMicroseconds(ambiguous));
  }

  private static readonly string SeparateTemp = @"Z:\scratch\temp";
  private static string SameVolumeTemp => Path.Combine(Path.GetTempPath(), "booksplice-temp");
  private static string DestinationRoot => Path.GetPathRoot(Path.GetTempPath())!;

  [Fact]
  public async Task SeparateTemporaryVolumeIsCheckedIndependently()
  {
    var analysis = Analysis(BookAnalysisStatus.Ready);
    var planner = (long? destination, long? temporary) => new ConversionPlanner(new OutputNamePlanner(), new Storage(new() { [DestinationRoot] = destination, ["Z:\\"] = temporary }), null, SeparateTemp);

    var ready = await planner(long.MaxValue, long.MaxValue).CreateAsync(analysis, Options(), CancellationToken.None);
    var space = ready.Plan!.Space;
    Assert.Equal("Z:\\", space.TemporaryVolume);
    Assert.Equal(DestinationRoot, space.DestinationVolume);
    Assert.Equal(space.TotalRequiredBytes, checked((long)decimal.Ceiling((space.FinalBytes + space.TemporaryBytes) * 1.10m)));
    Assert.True(space.DestinationRequiredBytes < space.TotalRequiredBytes);
    Assert.True(space.TemporaryRequiredBytes < space.TotalRequiredBytes);

    var tight = await planner(space.DestinationRequiredBytes, space.TemporaryRequiredBytes).CreateAsync(analysis, Options(), CancellationToken.None);
    Assert.NotNull(tight.Plan);
    Assert.Equal(0, tight.Plan.Space.MarginBytes);

    var smallTemp = await planner(long.MaxValue, space.TemporaryRequiredBytes - 1).CreateAsync(analysis, Options(), CancellationToken.None);
    Assert.Null(smallTemp.Plan);
    Assert.Contains(smallTemp.Diagnostics, d => d.Code == "planning.insufficient-temporary-space");
    Assert.NotNull(smallTemp.Space);
    Assert.Equal(space.FinalBytes, smallTemp.Space.FinalBytes);

    var smallDestination = await planner(space.DestinationRequiredBytes - 1, long.MaxValue).CreateAsync(analysis, Options(), CancellationToken.None);
    Assert.Contains(smallDestination.Diagnostics, d => d.Code == "planning.insufficient-space");

    var unknownTemp = await planner(long.MaxValue, null).CreateAsync(analysis, Options(), CancellationToken.None);
    Assert.Null(unknownTemp.Plan);
    Assert.Contains(unknownTemp.Diagnostics, d => d.Code == "planning.temporary-space-unavailable");
    Assert.Null(unknownTemp.Space!.TemporaryAvailableBytes);
  }

  [Fact]
  public async Task SharedVolumeKeepsOneCombinedRequirement()
  {
    var analysis = Analysis(BookAnalysisStatus.Ready);
    var combined = await new ConversionPlanner(new OutputNamePlanner(), new Storage(long.MaxValue), null, SameVolumeTemp).CreateAsync(analysis, Options(), CancellationToken.None);
    var space = combined.Plan!.Space;
    Assert.Null(space.TemporaryVolume);
    Assert.Equal(space.TotalRequiredBytes, space.DestinationRequiredBytes);
    Assert.Null(space.TemporaryRequiredBytes);

    var short1 = await new ConversionPlanner(new OutputNamePlanner(), new Storage(space.TotalRequiredBytes - 1), null, SameVolumeTemp.ToUpperInvariant()).CreateAsync(analysis, Options(), CancellationToken.None);
    Assert.Contains(short1.Diagnostics, d => d.Code == "planning.insufficient-space");
  }

  [Fact]
  public async Task MissingTemporaryDirectoryCountsEverythingOnDestination()
  {
    var result = await Planner().CreateAsync(Analysis(BookAnalysisStatus.Ready), Options(), CancellationToken.None);
    Assert.Null(result.Plan!.Space.TemporaryVolume);
    Assert.Equal(result.Plan.Space.TotalRequiredBytes, result.Plan.Space.DestinationRequiredBytes);
  }

  [Fact]
  public async Task RefusalKeepsEstimateAndStrategy()
  {
    var result = await Planner(1).CreateAsync(Analysis(BookAnalysisStatus.Ready), Options(), CancellationToken.None);
    Assert.Null(result.Plan);
    Assert.Equal(AudioStrategy.DirectTranscode, result.Strategy);
    Assert.NotEmpty(result.StrategyReasonCodes!);
    Assert.Equal(1, result.Space!.AvailableBytes);
    Assert.True(result.Space.TotalRequiredBytes > 1);
  }

  private static ConversionPlanner Planner(long? available = long.MaxValue) => new(new OutputNamePlanner(), new Storage(available));
  private static ConversionOptions Options(IReadOnlyDictionary<SemanticField, MetadataEdit>? edits = null, int? jobs = null, string? cover = null) => new(AppSettings.Defaults with { OutputDirectory = Path.GetTempPath(), ConversionJobs = jobs }, QualityProfileCatalog.Version1[0], edits, selectedCoverHash: cover);
  private static BookAnalysis Analysis(BookAnalysisStatus status, int count = 1, bool copy = false, CoverCandidate? cover = null)
  {
    var files = Enumerable.Range(1, count).Select(index => File($"{index:D2}.{(copy ? "m4a" : "mp3")}", copy)).ToArray();
    var fields = new Dictionary<SemanticField, AggregatedValue> { [SemanticField.BookTitle] = new(SemanticField.BookTitle, AggregationState.Consistent, "Title", []), [SemanticField.Author] = new(SemanticField.Author, AggregationState.Consistent, "Author", []) };
    return new BookAnalysis(status, "book", files, null, new BookMetadata(fields, new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()), new CoverDiscoveryResult(cover is null ? [] : [cover], [], cover), ChapterPlan.Valid([new ChapterEntry(0, 1_000_000, "One", "One", files[0].RelativePath)], 1_000_000), []);
  }
  private static SourceFile File(string path, bool copy) => new(path, path, new MediaProbeResult([new AudioTrack(0, copy ? "aac" : "mp3", "audio", 1, 44100, 1m, new Rational(1, 44100), new Dictionary<string, string>(), copy ? "LC" : null, "mono", 0m, "mp4a", copy ? "hash" : null, 64000)], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1m, copy ? "mov,mp4,m4a,3gp,3g2,mj2" : "mp3", 100));
  private static SourceFile File(string path, bool copy, int sampleRate, int channels) => new(path, path, new MediaProbeResult([new AudioTrack(0, copy ? "aac" : "mp3", "audio", channels, sampleRate, 1m, new Rational(1, sampleRate), new Dictionary<string, string>(), copy ? "LC" : null, channels == 1 ? "mono" : "stereo", 0m, "mp4a", copy ? "hash" : null, 64000)], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1m, copy ? "mov,mp4,m4a,3gp,3g2,mj2" : "mp3", 100));
  private static BookAnalysis AnalysisWithFormats(params (int SampleRate, int Channels)[] formats)
  {
    var files = formats.Select((format, index) => File($"{index:D2}.mp3", false, format.SampleRate, format.Channels)).ToArray();
    var fields = new Dictionary<SemanticField, AggregatedValue> { [SemanticField.BookTitle] = new(SemanticField.BookTitle, AggregationState.Consistent, "Title", []) };
    return new BookAnalysis(BookAnalysisStatus.Ready, "book", files, null, new BookMetadata(fields, new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()), new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([new ChapterEntry(0, formats.Length * 1_000_000L, "One", "One", files[0].RelativePath)], formats.Length * 1_000_000L), []);
  }
  private static BookAnalysis AnalysisWithDurations(params decimal?[] durations)
  {
    var baseAnalysis = Analysis(BookAnalysisStatus.Ready);
    var files = durations.Select((duration, index) =>
    {
      var file = File($"{index:D2}.mp3", false);
      return file with { ProbeResult = file.ProbeResult with { Duration = duration, AudioStreams = [file.ProbeResult.AudioStreams[0] with { Duration = duration }] } };
    }).ToArray();
    return new BookAnalysis(BookAnalysisStatus.Ready, "book", files, null, baseAnalysis.BookMetadata, baseAnalysis.Cover, ChapterPlan.Valid([], 0), []);
  }
  private sealed class Storage(long? available, Dictionary<string, long?>? byRoot = null) : IStorageSpaceProvider
  {
    public Storage(Dictionary<string, long?> byRoot) : this(null, byRoot) { }
    public long? GetAvailableBytes(string directory) => byRoot is null ? available : byRoot.GetValueOrDefault(Path.GetPathRoot(Path.GetFullPath(directory))!);
  }
  private sealed class TempProfiles : IDisposable
  {
    public TempProfiles() => Directory.CreateDirectory(Root);
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"BookSplice-{Guid.NewGuid():N}");
    public void Write(string name, string content) => System.IO.File.WriteAllText(Path.Combine(Root, name), content);
    public void Dispose() => Directory.Delete(Root, recursive: true);
  }
}
