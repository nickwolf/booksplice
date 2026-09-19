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
  private sealed class Storage(long? available) : IStorageSpaceProvider { public long? GetAvailableBytes(string directory) => available; }
}
