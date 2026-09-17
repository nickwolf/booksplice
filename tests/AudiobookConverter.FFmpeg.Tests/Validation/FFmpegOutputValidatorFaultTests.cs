using AudiobookConverter.Core.Analysis;
using AudiobookConverter.Core.Metadata;
using AudiobookConverter.Core.Naming;
using AudiobookConverter.Core.Planning;
using AudiobookConverter.Core.Settings;
using AudiobookConverter.FFmpeg.Execution;
using AudiobookConverter.FFmpeg.Tools;
using AudiobookConverter.FFmpeg.Validation;

namespace AudiobookConverter.FFmpeg.Tests.Validation;

public sealed class FFmpegOutputValidatorFaultTests
{
  [Fact]
  public async Task ValidateAsyncConvertsPacketInspectorLaunchFailureToStructuredCheck()
  {
    using var output = new TemporaryOutput();
    var validator = new FFmpegOutputValidator(new Probe(), new ThrowingPacketRunner(), new MediaToolSet("ffmpeg.exe", "ffprobe.exe", "pinned", "pinned"), new CoverPayloadValidator());

    var report = await validator.ValidateAsync(Plan(), output.Path, CancellationToken.None);

    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "packets.end-region" && !check.Passed);
    Assert.DoesNotContain("private", string.Join(' ', report.Errors), StringComparison.OrdinalIgnoreCase);
  }

  private static ConversionPlan Plan()
    => new(["source.mp3"], new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()), null, [], QualityProfileCatalog.Version1[0], ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, Path.Combine(Path.GetTempPath(), "Book.m4b"), AudioStrategy.DirectTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 1, "test", "GenericMp4", 44_100, 1);

  private sealed class Probe : IMediaProbe
  {
    public Task<MediaProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken = default)
      => Task.FromResult(new MediaProbeResult([new AudioTrack(0, "aac", "audio", 1, 44_100, 1m, new Rational(1, 44_100), new Dictionary<string, string>(), "LC", "mono")], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1m));
  }

  private sealed class ThrowingPacketRunner : IProcessRunner
  {
    public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken)
      => throw new InvalidOperationException("C:\\private\\packet-inspector.exe failed");
  }

  private sealed class TemporaryOutput : IDisposable
  {
    public TemporaryOutput() { Path = System.IO.Path.GetTempFileName(); File.Move(Path, Path + ".m4b"); Path += ".m4b"; }
    public string Path { get; }
    public void Dispose() { try { File.Delete(Path); } catch { } }
  }
}
