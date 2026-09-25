using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.Core.Validation;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tools;
using BookSplice.FFmpeg.Validation;

namespace BookSplice.FFmpeg.Tests.Validation;

public sealed class FFmpegOutputValidatorTests
{
  [Fact]
  public async Task ValidateAsyncReturnsStructuredFailuresForMissingFile()
  {
    var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.m4b");
    var report = await Validator(new Probe()).ValidateAsync(Plan(), path, CancellationToken.None);

    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "file.exists" && !check.Passed);
  }

  [Fact]
  public async Task ValidateAsyncConvertsProbeExceptionToSanitizedMalformedContainerCheck()
  {
    using var output = new TemporaryOutput();
    var report = await Validator(new Probe(exception: new InvalidOperationException("C:\\private\\book.m4b failed"))).ValidateAsync(Plan(), output.Path, CancellationToken.None);

    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "container.parse" && !check.Passed);
    Assert.DoesNotContain("private", string.Join(' ', report.Errors), StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task ValidateAsyncReportsEachAudioContractFailureWithoutCollapsingThem()
  {
    using var output = new TemporaryOutput();
    var facts = Media(audio: [Track("mp3", 2, 48_000, "stereo"), Track("aac", 2, 48_000, "stereo")]);
    var report = await Validator(new Probe(facts)).ValidateAsync(Plan(sampleRate: 44_100, channels: 1), output.Path, CancellationToken.None);

    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "audio.primary-count" && !check.Passed);
    Assert.Contains(report.Checks, check => check.Code == "audio.codec" && !check.Passed);
    Assert.Contains(report.Checks, check => check.Code == "audio.sample-rate" && !check.Passed);
    Assert.Contains(report.Checks, check => check.Code == "audio.layout" && !check.Passed);
  }

  [Fact]
  public async Task ValidateAsyncDetectsTruncatedDurationNonmonotonicChaptersMissingTagsAndExtraAudio()
  {
    using var output = new TemporaryOutput();
    var facts = Media(
      audio: [Track(), Track()],
      duration: 1m,
      chapters: [new MediaChapter(0, 0m, 2m, new Rational(1, 1), new Dictionary<string, string>()), new MediaChapter(1, 1m, 3m, new Rational(1, 1), new Dictionary<string, string>())],
      tags: new Dictionary<string, string>());
    var report = await Validator(new Probe(facts)).ValidateAsync(Plan(), output.Path, CancellationToken.None);

    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "duration.expected" && !check.Passed);
    Assert.Contains(report.Checks, check => check.Code == "chapters.monotonic" && !check.Passed);
    Assert.Contains(report.Checks, check => check.Code == "metadata.required" && !check.Passed);
    Assert.Contains(report.Checks, check => check.Code == "audio.extra" && !check.Passed);
  }

  [Fact]
  public async Task ValidateAsyncRejectsSilentlyTruncatedChapterTitle()
  {
    using var output = new TemporaryOutput();
    var longTitle = new string('T', 256);
    var plan = Plan(chapterTitle: longTitle);
    var truncated = new MediaChapter(0, 0m, 4m, new Rational(1, 1), new Dictionary<string, string> { ["title"] = longTitle[..255] });

    var report = await Validator(new Probe(Media(chapters: [truncated]))).ValidateAsync(plan, output.Path, CancellationToken.None);

    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "chapters.titles" && !check.Passed);
  }

  [Theory]
  [InlineData("4.023", true)]
  [InlineData("4.100", false)]
  public async Task ValidateAsyncAllowsAacFrameRoundingAtFinalChapterEnd(string actualEndText, bool expectedPass)
  {
    using var output = new TemporaryOutput();
    var actualEnd = decimal.Parse(actualEndText, System.Globalization.CultureInfo.InvariantCulture);
    var chapter = new MediaChapter(0, 0m, actualEnd, new Rational(1, 1_000_000), new Dictionary<string, string> { ["title"] = "One" });

    var report = await Validator(new Probe(Media(duration: actualEnd, chapters: [chapter]))).ValidateAsync(Plan(), output.Path, CancellationToken.None);

    Assert.Equal(expectedPass, report.Checks.Single(check => check.Code == "chapters.timing").Passed);
  }

  [Theory]
  [InlineData(1_000_000, 1, 2_000_000)]
  [InlineData(10_000_000_000, 100, 10_000_000)]
  public void DurationToleranceUsesTheDocumentedMaximum(long expectedUs, int files, long minimumToleranceUs)
    => Assert.True(DurationTolerance.Calculate(expectedUs, files) >= minimumToleranceUs);

  [Fact]
  public async Task ValidateAsyncRunsNoDecodeForLightweightValidation()
  {
    using var output = new TemporaryOutput();
    var runner = new Runner();
    var report = await Validator(new Probe(Media()), runner).ValidateAsync(Plan(), output.Path, CancellationToken.None);

    Assert.True(report.IsValid);
    Assert.DoesNotContain(runner.Specs, spec => spec.Arguments.Contains("-f") && spec.Arguments.Contains("null"));
  }

  [Fact]
  public async Task ValidateAsyncReportsDecodeFailureAndUsesNullMuxerInFullMode()
  {
    using var output = new TemporaryOutput();
    var runner = new Runner { DecodeResult = new ProcessResult(1, "", "decode error") };
    var report = await Validator(new Probe(Media()), runner).ValidateAsync(Plan(level: ValidationLevel.Full), output.Path, CancellationToken.None);

    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "decode.full" && !check.Passed);
    Assert.Contains(runner.Specs, spec => spec.Arguments.Contains("-f") && spec.Arguments.Contains("null") && spec.Arguments.Contains(output.Path));
  }

  [Fact]
  public async Task ValidateAsyncAcceptsNickMp3tagAlbumArtistProbeAliasAndRequiresPreservedTags()
  {
    using var output = new TemporaryOutput();
    var metadata = new BookMetadata(
      new Dictionary<SemanticField, AggregatedValue>
      {
        [SemanticField.BookTitle] = new(SemanticField.BookTitle, AggregationState.Consistent, "Book", []),
        [SemanticField.Author] = new(SemanticField.Author, AggregationState.Consistent, "Author", []),
      },
      new Dictionary<string, string> { ["CUSTOM"] = "Value" },
      new Dictionary<SemanticField, IReadOnlyList<string>>());
    var tags = new Dictionary<string, string>
    {
      ["TITLE"] = "Book",
      ["ALBUM"] = "Book",
      ["ARTIST"] = "Author",
      ["album_artist"] = "Author",
    };

    var missingPreserved = await Validator(new Probe(Media(tags: tags))).ValidateAsync(
      Plan(metadataProfileId: "NickMp3tag", metadata: metadata), output.Path, CancellationToken.None);

    Assert.False(missingPreserved.IsValid);
    tags["CUSTOM"] = "Value";

    var report = await Validator(new Probe(Media(tags: tags))).ValidateAsync(
      Plan(metadataProfileId: "NickMp3tag", metadata: metadata), output.Path, CancellationToken.None);

    Assert.True(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "metadata.required" && check.Passed);
  }
  private static FFmpegOutputValidator Validator(IMediaProbe probe, Runner? runner = null)
    => new(probe, runner ?? new Runner(), new MediaToolSet("ffmpeg.exe", "ffprobe.exe", "pinned", "pinned"), new CoverPayloadValidator());

  private static ConversionPlan Plan(string? output = null, ValidationLevel level = ValidationLevel.Lightweight, int sampleRate = 44_100, int channels = 1, string metadataProfileId = "GenericMp4", BookMetadata? metadata = null, string chapterTitle = "One")
  {
    metadata ??= new BookMetadata(new Dictionary<SemanticField, AggregatedValue> { [SemanticField.BookTitle] = new(SemanticField.BookTitle, AggregationState.Consistent, "Book", []) }, new Dictionary<string, string> { ["CUSTOM"] = "Value" }, new Dictionary<SemanticField, IReadOnlyList<string>>());
    return new ConversionPlan(["source-01.mp3"], metadata, null, [new ChapterEntry(0, 4_000_000, chapterTitle, "", "source-01.mp3")], QualityProfileCatalog.Version1[0], level, CollisionPolicy.AvoidCollision, output ?? Path.Combine(Path.GetTempPath(), "Book.m4b"), AudioStrategy.DirectTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 1, "test", metadataProfileId, sampleRate, channels);
  }

  private static MediaProbeResult Media(IReadOnlyList<AudioTrack>? audio = null, decimal duration = 4m, IReadOnlyList<MediaChapter>? chapters = null, IReadOnlyDictionary<string, string>? tags = null)
    => new(audio ?? [Track()], [], chapters ?? [new MediaChapter(0, 0m, 4m, new Rational(1, 1), new Dictionary<string, string> { ["title"] = "One" })], new TagCollection(tags ?? new Dictionary<string, string> { ["TITLE"] = "Book", ["ALBUM"] = "Book", ["CUSTOM"] = "Value" }), tags ?? new Dictionary<string, string> { ["TITLE"] = "Book", ["ALBUM"] = "Book", ["CUSTOM"] = "Value" }, [], duration, "mov,mp4,m4a,3gp,3g2,mj2", 1);
  private static AudioTrack Track(string codec = "aac", int channels = 1, int sampleRate = 44_100, string layout = "mono") => new(0, codec, "audio", channels, sampleRate, 4m, new Rational(1, sampleRate), new Dictionary<string, string>(), "LC", layout, 0m, "mp4a", null, 64_000);

  private sealed class Probe(MediaProbeResult? result = null, Exception? exception = null) : IMediaProbe
  {
    public Task<MediaProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken = default) => exception is null ? Task.FromResult(result ?? Media()) : Task.FromException<MediaProbeResult>(exception);
  }

  private sealed class Runner : IProcessRunner
  {
    public List<ProcessSpec> Specs { get; } = [];
    public ProcessResult DecodeResult { get; set; } = new(0, "", "");
    public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken) { Specs.Add(spec); return Task.FromResult(spec.Arguments.Contains("null") ? DecodeResult : new ProcessResult(0, "packet", "")); }
  }

  private sealed class TemporaryOutput : IDisposable
  {
    public TemporaryOutput() { Path = System.IO.Path.GetTempFileName(); File.Move(Path, Path + ".m4b"); Path += ".m4b"; }
    public string Path { get; }
    public void Dispose() { try { File.Delete(Path); } catch { } }
  }
}
