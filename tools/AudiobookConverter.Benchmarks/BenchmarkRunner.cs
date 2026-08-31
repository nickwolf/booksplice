using System.Diagnostics;
using System.Globalization;
using AudiobookConverter.Core.Analysis;
using AudiobookConverter.FFmpeg.Execution;
using AudiobookConverter.FFmpeg.Probing;
using AudiobookConverter.FFmpeg.Tools;

namespace AudiobookConverter.Benchmarks;

public sealed class BenchmarkRunner(IProcessRunner runner, IMediaProbe probe, MediaToolSet tools)
{
  public async Task<BenchmarkResult> RunAsync(BenchmarkCase testCase, string outputPath, BenchmarkRunOptions? options = null, CancellationToken cancellationToken = default)
  {
    options ??= new BenchmarkRunOptions();
    MediaProbeResult? source = null; MediaProbeResult? output = null; ProcessResult? process = null; string? inputPath = null; string? concat = null;
    var stopwatch = new Stopwatch();
    try
    {
      (source, inputPath, concat) = await PrepareSourceAsync(testCase, cancellationToken).ConfigureAwait(false);
      ValidateSource(testCase, source);
      stopwatch.Start();
      var channelArguments = options.ChannelMode switch { "mono" => new[] { "-ac", "1" }, "stereo" => new[] { "-ac", "2" }, _ => Array.Empty<string>() };
      var codecArguments = options.StreamCopy ? new[] { "-c:a", "copy" } : new[] { "-c:a", "aac", "-b:a", options.TargetBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k" };
      process = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-y", .. (concat is null ? new[] { "-i", inputPath } : new[] { "-f", "concat", "-safe", "0", "-i", inputPath }), "-map", "0:a:0", .. codecArguments, .. channelArguments, outputPath]), null, cancellationToken).ConfigureAwait(false);
      stopwatch.Stop();
      if (process.ExitCode != 0) return Result(testCase, source, null, process, stopwatch, testCase.ExpectedCorrupt ? "expected-failure" : "failed", testCase.ExpectedCorrupt ? "corrupt input rejected by encoder" : $"ffmpeg exit code {process.ExitCode}", outputPath);
      output = await probe.ProbeAsync(outputPath, cancellationToken).ConfigureAwait(false);
      var validation = ValidateOutput(source, output, outputPath);
      if (validation is null && options.ValidationMode == "full-decode") validation = await DecodeToNullAsync(outputPath, cancellationToken).ConfigureAwait(false);
      return Result(testCase, source, output, process, stopwatch, validation ?? "ok", validation is null ? "" : "output validation failed", outputPath, options);
    }
    catch (OperationCanceledException)
    {
      stopwatch.Stop(); return Result(testCase, source, output, process, stopwatch, "failed", "benchmark cancelled", outputPath);
    }
    catch (Exception)
    {
      stopwatch.Stop(); return Result(testCase, source, output, process, stopwatch, testCase.ExpectedCorrupt ? "expected-failure" : "failed", testCase.ExpectedCorrupt ? "corrupt input rejected during probe or encode" : "benchmark processing failed", outputPath);
    }
    finally { if (concat is not null && File.Exists(concat)) File.Delete(concat); }
  }

  private async Task<(MediaProbeResult Source, string Input, string? Concat)> PrepareSourceAsync(BenchmarkCase testCase, CancellationToken cancellationToken)
  {
    if (!Directory.Exists(testCase.SourcePath)) return (await probe.ProbeAsync(testCase.SourcePath, cancellationToken).ConfigureAwait(false), testCase.SourcePath, null);
    var names = Directory.EnumerateFiles(testCase.SourcePath).Select(Path.GetFileName).Where(name => name is not null).Cast<string>().Order(StringComparer.Ordinal).ToArray();
    if (!names.SequenceEqual(testCase.ExpectedOrder, StringComparer.Ordinal)) throw new InvalidDataException("track order did not match corpus expectation");
    var sources = await Task.WhenAll(names.Select(name => probe.ProbeAsync(Path.Combine(testCase.SourcePath, name), cancellationToken))).ConfigureAwait(false);
    var first = sources.Length > 0 ? sources[0] : throw new InvalidDataException("track directory was empty");
    var duration = sources.Sum(item => item.Duration ?? 0m);
    var aggregate = new MediaProbeResult(first.AudioStreams, first.AttachedPictures, first.Chapters, first.FormatTags, first.RawTags, first.Warnings, duration);
    var concat = Path.Combine(Path.GetTempPath(), $"audiobookconverter-{Guid.NewGuid():N}.ffconcat");
    await File.WriteAllLinesAsync(concat, names.Select(name => "file '" + Path.Combine(testCase.SourcePath, name).Replace("'", "'\\''") + "'"), cancellationToken).ConfigureAwait(false);
    return (aggregate, concat, concat);
  }

  private static void ValidateSource(BenchmarkCase testCase, MediaProbeResult source)
  {
    if (testCase.ExpectedProperties is null) return;
    var audio = source.AudioStreams.Count > 0 ? source.AudioStreams[0] : null;
    var cover = source.AttachedPictures.Count > 0 ? source.AttachedPictures[0] : null;
    if (audio is null || !Matches(testCase, "codec", audio.CodecName) || !Matches(testCase, "channels", audio.Channels?.ToString(CultureInfo.InvariantCulture)) || !Matches(testCase, "sampleRate", audio.SampleRate?.ToString(CultureInfo.InvariantCulture)) || !Matches(testCase, "coverCodec", cover?.CodecName) || !Matches(testCase, "chapters", source.Chapters.Count.ToString(CultureInfo.InvariantCulture)) || !Matches(testCase, "trackCount", testCase.ExpectedOrder.Count.ToString(CultureInfo.InvariantCulture))) throw new InvalidDataException("source did not match corpus expectation");
  }

  private static bool Matches(BenchmarkCase item, string key, string? actual) => item.ExpectedProperties is null || !item.ExpectedProperties.TryGetValue(key, out var expected) || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
  private static string? ValidateOutput(MediaProbeResult source, MediaProbeResult output, string outputPath) => !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0 || output.AudioStreams.Count == 0 || source.Duration is not > 0 || output.Duration is not > 0 || Math.Abs(source.Duration.Value - output.Duration.Value) > 0.25m ? "failed" : null;
  private async Task<string?> DecodeToNullAsync(string outputPath, CancellationToken cancellationToken)
  {
    var decode = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-i", outputPath, "-f", "null", "-"]), null, cancellationToken).ConfigureAwait(false);
    return decode.ExitCode == 0 ? null : "full decode validation failed";
  }

  private static BenchmarkResult Result(BenchmarkCase item, MediaProbeResult? source, MediaProbeResult? output, ProcessResult? process, Stopwatch watch, string status, string reason, string outputPath, BenchmarkRunOptions? options = null)
  {
    var input = source is { AudioStreams.Count: > 0 } ? source.AudioStreams[0] : null; var audio = output is { AudioStreams.Count: > 0 } ? output.AudioStreams[0] : null; var bytes = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
    var wall = (decimal?)watch.Elapsed.TotalSeconds;
    options ??= new BenchmarkRunOptions();
    return new BenchmarkResult(BenchmarkResult.NewRunId(), item.CaseId, source?.Duration, input?.CodecName, input?.Channels, input?.SampleRate, wall, (decimal?)process?.ChildCpuTime?.TotalSeconds, audio?.CodecName, output?.AudioStreams.Count, audio?.Channels, audio?.SampleRate, output?.Duration, bytes == 0 ? null : bytes, source?.Duration is > 0 && wall is > 0 ? source.Duration / wall : null, status, reason, options.Strategy, options.TargetBitrateKbps, options.ChannelMode, options.ValidationMode, options.Concurrency, null, options.StorageClass);
  }
}
