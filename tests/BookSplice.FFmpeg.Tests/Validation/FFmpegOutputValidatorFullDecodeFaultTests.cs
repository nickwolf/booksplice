using BookSplice.Core.Analysis;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tools;
using BookSplice.FFmpeg.Validation;

namespace BookSplice.FFmpeg.Tests.Validation;

public sealed class FFmpegOutputValidatorFullDecodeFaultTests
{
  [Fact]
  public async Task ValidateAsyncConvertsFullDecodeLaunchFailureToStructuredCheck()
  {
    using var output = new TemporaryOutput();
    var validator = new FFmpegOutputValidator(new Probe(), new ThrowingDecodeRunner(), new MediaToolSet("ffmpeg.exe", "ffprobe.exe", "pinned", "pinned"), new CoverPayloadValidator());

    var report = await validator.ValidateAsync(Plan(), output.Path, CancellationToken.None);

    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "decode.full" && !check.Passed);
    Assert.DoesNotContain("private", string.Join(' ', report.Errors), StringComparison.OrdinalIgnoreCase);
  }

  private static ConversionPlan Plan()
    => new(["source.mp3"], new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()), null, [], QualityProfileCatalog.Version1[0], ValidationLevel.Full, CollisionPolicy.AvoidCollision, Path.Combine(Path.GetTempPath(), "Book.m4b"), AudioStrategy.DirectTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 1, "test", "GenericMp4", 44_100, 1);

  private sealed class Probe : IMediaProbe
  {
    public Task<MediaProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken = default)
      => Task.FromResult(new MediaProbeResult([new AudioTrack(0, "aac", "audio", 1, 44_100, 1m, new Rational(1, 44_100), new Dictionary<string, string>(), "LC", "mono")], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1m));
  }

  private sealed class ThrowingDecodeRunner : IProcessRunner
  {
    public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken)
      => spec.Arguments.Contains("null")
        ? throw new InvalidOperationException("C:\\private\\ffmpeg.exe failed")
        : Task.FromResult(new ProcessResult(0, "packet", ""));
  }

  private sealed class TemporaryOutput : IDisposable
  {
    public TemporaryOutput() { Path = System.IO.Path.GetTempFileName(); File.Move(Path, Path + ".m4b"); Path += ".m4b"; }
    public string Path { get; }
    public void Dispose() { try { File.Delete(Path); } catch { } }
  }
}
