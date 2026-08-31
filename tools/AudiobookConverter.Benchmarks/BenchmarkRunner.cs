using System.Diagnostics;
using AudiobookConverter.Core.Analysis;
using AudiobookConverter.FFmpeg.Execution;
using AudiobookConverter.FFmpeg.Probing;
using AudiobookConverter.FFmpeg.Tools;

namespace AudiobookConverter.Benchmarks;

public sealed class BenchmarkRunner(IProcessRunner runner, IMediaProbe probe, MediaToolSet tools)
{
  public async Task<BenchmarkResult> RunAsync(BenchmarkCase testCase, string outputPath, CancellationToken cancellationToken = default)
  {
    var source = await probe.ProbeAsync(testCase.SourcePath, cancellationToken).ConfigureAwait(false);
    var input = source.AudioStreams.Count > 0 ? source.AudioStreams[0] : null;
    var stopwatch = Stopwatch.StartNew();
    var process = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-y", "-i", testCase.SourcePath, "-c:a", "aac", "-b:a", "128k", outputPath]), null, cancellationToken).ConfigureAwait(false);
    stopwatch.Stop();
    MediaProbeResult? output = null;
    var status = process.ExitCode == 0 ? "ok" : "failed";
    if (process.ExitCode == 0) output = await probe.ProbeAsync(outputPath, cancellationToken).ConfigureAwait(false);
    var sourceSeconds = source.Duration;
    var wall = (decimal)stopwatch.Elapsed.TotalSeconds;
    var outputAudio = output is { AudioStreams.Count: > 0 } ? output.AudioStreams[0] : null;
    return new BenchmarkResult(Guid.NewGuid().ToString("N"), testCase.CaseId, sourceSeconds, input?.CodecName, input?.Channels, input?.SampleRate, wall, (decimal?)process.ChildCpuTime?.TotalSeconds, outputAudio?.CodecName, output?.AudioStreams.Count, File.Exists(outputPath) ? new FileInfo(outputPath).Length : null, sourceSeconds is > 0 ? sourceSeconds / wall : null, status);
  }
}
