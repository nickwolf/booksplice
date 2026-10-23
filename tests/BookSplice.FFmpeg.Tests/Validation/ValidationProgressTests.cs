using BookSplice.Core.Analysis;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tools;
using BookSplice.FFmpeg.Validation;

namespace BookSplice.FFmpeg.Tests.Validation;

public sealed class ValidationProgressTests
{
  [Fact]
  public async Task ChecksEnterBeforeProbe()
  {
    using var output = new TemporaryOutput();
    var progress = new Capture();
    var probe = new Probe(() => Assert.Contains(progress.Values, value => value is { Phase: ConversionPhase.ValidationChecks, IsPhaseEntry: true }));

    await Validator(probe, new Runner()).ValidateAsync(Plan(ValidationLevel.Lightweight), output.Path, CancellationToken.None, progress);

    Assert.Equal(ConversionPhase.ValidationChecks, progress.Values[0].Phase);
    Assert.Null(progress.Values[0].PhasePercent);
    Assert.False(progress.Values[0].ExpectsMeasurableUpdates);
  }

  [Fact]
  public async Task LightweightSkipsFullDecodePhase()
  {
    using var output = new TemporaryOutput();
    var progress = new Capture();
    await Validator(new Probe(), new Runner()).ValidateAsync(Plan(ValidationLevel.Lightweight), output.Path, CancellationToken.None, progress);
    Assert.DoesNotContain(progress.Values, value => value.Phase == ConversionPhase.FullDecode);

    var failed = new Capture();
    await Validator(new Probe(() => throw new InvalidOperationException("parse failed")), new Runner()).ValidateAsync(Plan(ValidationLevel.Full), output.Path, CancellationToken.None, failed);
    Assert.DoesNotContain(failed.Values, value => value.Phase == ConversionPhase.FullDecode);
  }

  [Theory]
  [InlineData("8", 50d, 2d)]
  [InlineData(null, null, null)]
  public async Task FullDecodeUsesOutputDurationWithoutChapters(string? duration, double? percent, double? speed)
  {
    using var output = new TemporaryOutput();
    var clock = new Clock();
    var progress = new Capture();
    var runner = new Runner { OnDecode = callback => { clock.Advance(TimeSpan.FromSeconds(2)); callback?.Report("out_time_us=4000000"); } };
    var facts = Facts(duration is null ? null : decimal.Parse(duration, System.Globalization.CultureInfo.InvariantCulture));

    await Validator(new Probe(result: facts), runner, clock).ValidateAsync(Plan(ValidationLevel.Full), output.Path, CancellationToken.None, progress);

    var entry = Assert.Single(progress.Values, value => value is { Phase: ConversionPhase.FullDecode, IsPhaseEntry: true });
    Assert.Equal(percent is not null, entry.ExpectsMeasurableUpdates);
    var sample = Assert.Single(progress.Values, value => value is { Phase: ConversionPhase.FullDecode, IsPhaseEntry: false } && value.PhasePercent == percent);
    Assert.Equal(speed, sample.PhaseSpeed);
    Assert.Null(sample.OutTimeMicroseconds);
    Assert.Null(sample.Speed);
    Assert.Null(sample.State);
    Assert.Null(sample.DisplayPercent);
    Assert.Contains("-progress", runner.DecodeSpec!.Arguments);
    Assert.Contains("pipe:1", runner.DecodeSpec.Arguments);
    Assert.Contains("-nostats", runner.DecodeSpec.Arguments);
  }

  [Fact]
  public async Task FullDecodePercentDoesNotProveValidationPassed()
  {
    using var output = new TemporaryOutput();
    var progress = new Capture();
    var runner = new Runner
    {
      DecodeResult = new ProcessResult(1, "", "Invalid data in C:\\Private Library\\Book.m4b"),
      OnDecode = callback => callback?.Report("out_time_us=8000000")
    };

    var report = await Validator(new Probe(result: Facts(8m)), runner).ValidateAsync(Plan(ValidationLevel.Full), output.Path, CancellationToken.None, progress);

    Assert.Contains(progress.Values, value => value.Phase == ConversionPhase.FullDecode && value.PhasePercent == 100d);
    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "decode.full" && !check.Passed && check.Required);
    Assert.Contains(report.Warnings, warning => warning.Contains("Invalid data") && !warning.Contains("Private Library"));
    Assert.Contains(report.ToolDiagnostics, diagnostic => diagnostic.Contains("Private Library"));
    var count = progress.Values.Count;
    runner.DecodeCallback?.Report("out_time_us=8000000");
    Assert.Equal(count, progress.Values.Count);
  }

  private static FFmpegOutputValidator Validator(IMediaProbe probe, Runner runner, TimeProvider? clock = null)
    => new(probe, runner, new MediaToolSet("ffmpeg.exe", "ffprobe.exe", "pinned", "pinned"), new CoverPayloadValidator(), clock);

  private static ConversionPlan Plan(ValidationLevel level)
    => new(["source.mp3"], new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()), null, [], QualityProfileCatalog.Version1[0], level, CollisionPolicy.AvoidCollision, Path.Combine(Path.GetTempPath(), "Book.m4b"), AudioStrategy.DirectTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 1, "test", MetadataProfiles.GenericMp4, 44_100, 1);

  private static MediaProbeResult Facts(decimal? duration)
    => new([new AudioTrack(0, "aac", "audio", 1, 44_100, duration, new Rational(1, 44_100), new Dictionary<string, string>(), "LC", "mono")], [], [], new TagCollection([]), new Dictionary<string, string>(), [], duration);

  private sealed class Capture : IProgress<ConversionProgress>
  {
    public List<ConversionProgress> Values { get; } = [];
    public void Report(ConversionProgress value) => Values.Add(value);
  }

  private sealed class Probe(Action? onProbe = null, MediaProbeResult? result = null) : IMediaProbe
  {
    public Task<MediaProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken = default)
    {
      onProbe?.Invoke();
      return Task.FromResult(result ?? Facts(8m));
    }
  }

  private sealed class Runner : IProcessRunner
  {
    public Action<IProgress<string>?>? OnDecode { get; init; }
    public ProcessResult DecodeResult { get; init; } = new(0, "", "");
    public IProgress<string>? DecodeCallback { get; private set; }
    public ProcessSpec? DecodeSpec { get; private set; }
    public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken)
    {
      if (!spec.Arguments.Contains("null")) return Task.FromResult(new ProcessResult(0, "packet", ""));
      DecodeSpec = spec;
      DecodeCallback = progress;
      OnDecode?.Invoke(progress);
      return Task.FromResult(DecodeResult);
    }
  }

  private sealed class Clock : TimeProvider
  {
    private long _timestamp;
    public override long GetTimestamp() => _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
  }

  private sealed class TemporaryOutput : IDisposable
  {
    public TemporaryOutput() { Path = System.IO.Path.GetTempFileName(); File.Move(Path, Path + ".m4b"); Path += ".m4b"; }
    public string Path { get; }
    public void Dispose() { try { File.Delete(Path); } catch { } }
  }
}
