using System.Globalization;
using System.Text.Json;
using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Ordering;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.FFmpeg.Commands;
using BookSplice.FFmpeg.Covers;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Tools;
using BookSplice.FFmpeg.Validation;

namespace BookSplice.FFmpeg.Tests.Execution;

public sealed class SegmentedJoinIntegrationTests
{
  private const int Count = 33;
  private static readonly int[] MixedRates = [22_050, 32_000, 48_000, 44_100];

  [PinnedMediaFact]
  public Task SegmentedOutputHasExactlyTheSourceSamplesAndEachChapterStartsOnItsTone() => RunAsync(Enumerable.Repeat(44_100, Count).ToArray(), 44_100);

  [PinnedMediaFact]
  public Task MixedSampleRatesAreResampledAndStillJoinWithoutDrift() => RunAsync(Enumerable.Range(0, Count).Select(index => MixedRates[index % MixedRates.Length]).ToArray(), 44_100);

  [PinnedMediaFact]
  public Task UniformFortyEightKilohertzSourcesKeepTheirRateAndJoinExactly() => RunAsync(Enumerable.Repeat(48_000, Count).ToArray(), 48_000);

  private static async Task RunAsync(int[] rates, int outputRate)
  {
    var tools = ResolveTools();
    var root = Directory.CreateTempSubdirectory("booksplice-segmented-join-").FullName;
    try
    {
      var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
      var output = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;
      var frequencies = Enumerable.Range(0, Count).Select(index => 300 + index * 7 % Count * 100).ToArray();
      var paths = Enumerable.Range(0, Count).Select(index => Path.Combine(source, $"{index:D2}.mp3")).ToArray();
      await Task.WhenAll(paths.Select((path, index) => GenerateAsync(tools, path, frequencies[index], rates[index], 1.0 + 0.0173 * index)));
      var counts = await Task.WhenAll(paths.Select(path => DecodedSamplesAsync(tools, root, [path], 1, outputRate)));
      var starts = new long[Count];
      for (var index = 1; index < Count; index++) starts[index] = starts[index - 1] + counts[index - 1];
      var total = counts.Sum();
      Assert.All(counts, count => Assert.NotEqual(0, count % 1024));

      var runner = new ProcessRunner();
      var probe = new FFprobeMediaProbe(runner, tools);
      var analyzer = new BookAnalyzer(new SourceDiscoverer(probe, 4), new OrderResolver(), new MetadataAggregator(), new CoverDiscoverer(new MediaCoverPayloadOpener(tools)), new ChapterPlanner());
      var service = new ConversionService(analyzer, new ConversionPlanner(new OutputNamePlanner(), new FixedSpace()),
        new FFmpegConversionExecutor(runner, new FFmpegCommandFactory(tools), new Mp4MetadataWriter(), Path.Combine(root, "jobs")),
        new FFmpegOutputValidator(probe, runner, tools, new CoverPayloadValidator()), new AtomicPublisher(),
        new TemporaryArtifactCleaner(Path.Combine(root, "jobs")), new JsonJobLogWriter(Path.Combine(root, "logs")));
      var settings = AppSettings.Defaults with { OutputDirectory = output, ValidationLevel = ValidationLevel.Full, ConversionJobs = 2 };
      var result = await service.ConvertAsync(new ConversionRequest(source, true, new ConversionOptions(settings, QualityProfileCatalog.Version1[2], omitCover: true)), CancellationToken.None);

      Assert.Equal(ConversionTerminalStatus.Succeeded, result.Status);
      var plan = result.Planning!.Plan!;
      Assert.Equal(AudioStrategy.SegmentedTranscode, plan.Strategy);
      Assert.Equal(outputRate, plan.SampleRate);
      Assert.True(result.Validation!.IsValid, string.Join(Environment.NewLine, result.Validation.Checks.Where(check => !check.Passed).Select(check => $"{check.Code}: {check.Message}")));

      var raw = Path.Combine(root, "output.f32");
      var decoded = await Run(tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-y", "-i", result.PublishedPath!, "-map", "0:a:0", "-ac", "1", "-f", "f32le", raw]);
      Assert.Equal(0, decoded.ExitCode);
      var samples = ReadFloats(raw);
      Assert.Equal(total, samples.Length);

      Assert.Equal(Count, plan.Chapters.Count);
      var chapterTolerance = rates.All(rate => rate == outputRate) ? 2 : 16;
      for (var index = 1; index < Count; index++)
      {
        var chapter = plan.Chapters[index].StartMicroseconds * outputRate / 1_000_000;
        Assert.True(Tone(samples, chapter + 3000, frequencies[index], outputRate) > 10 * Tone(samples, chapter + 3000, frequencies[index - 1], outputRate), $"chapter {index} does not start on its tone");
        Assert.True(Tone(samples, chapter - 7096, frequencies[index - 1], outputRate) > 10 * Tone(samples, chapter - 7096, frequencies[index], outputRate), $"chapter {index} is not preceded by the previous tone");
        Assert.InRange(chapter - starts[index], -chapterTolerance, chapterTolerance);
      }
      var reference = Path.Combine(root, "reference.f32");
      var rebuilt = await DecodedSamplesAsync(tools, root, paths, Count, outputRate, reference);
      Assert.Equal(total, rebuilt);
      var truth = ReadFloats(reference);
      // A steady tone compares badly against the sources if the output is shifted by even a fraction of an encoder frame.
      for (var index = 1; index < Count; index++)
      {
        var before = SignalToNoise(samples, truth, starts[index] - 2048 - 4096, 4096);
        var after = SignalToNoise(samples, truth, starts[index] + 2048, 4096);
        Assert.True(before > 15 && after > 15, $"the output drifts from the sources at source {index} ({before:F1} dB before, {after:F1} dB after)");
      }

      var cuts = SegmentRangePlanner.Plan(counts, settings.ConversionJobs!.Value).SkipLast(1).Select(range => range.End).ToArray();
      Assert.NotEmpty(cuts);
      var clear = cuts.Where(cut => starts.All(start => Math.Abs(start - cut) > 6144)).ToArray();
      Assert.NotEmpty(clear);
      foreach (var cut in clear)
      {
        var snr = SignalToNoise(samples, truth, cut - 2048, 4096);
        Assert.True(snr > 15, $"the join at sample {cut} does not match the sources ({snr:F1} dB)");
      }

      var streams = await ProbeStreamsAsync(tools, result.PublishedPath!);
      Assert.Equal($"1/{outputRate}", Assert.Single(streams, stream => stream.Type is "data" or "subtitle").TimeBase);
    }
    finally { Directory.Delete(root, true); }
  }

  private static async Task GenerateAsync(MediaToolSet tools, string path, int frequency, int rate, double seconds)
  {
    var directory = Environment.GetEnvironmentVariable("BOOKSPLICE_FIXTURE_FFMPEG_DIR");
    var generator = string.IsNullOrWhiteSpace(directory) ? tools.FFmpegPath : new MediaToolLocator(directory).Resolve().FFmpegPath;
    var result = await Run(generator, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency={frequency}:sample_rate={rate}:duration={seconds.ToString("F4", CultureInfo.InvariantCulture)}", "-ac", "1", "-c:a", "libmp3lame", "-b:a", "64k", path]);
    Assert.Equal(0, result.ExitCode);
  }

  // The samples the converter's per-source chain yields, counted from the decoded bytes (out_time and durations are not sample counts).
  private static async Task<long> DecodedSamplesAsync(MediaToolSet tools, string root, string[] inputs, int expectedInputs, int outputRate, string? keep = null)
  {
    var raw = keep ?? Path.Combine(root, $"count-{Guid.NewGuid():N}.f32");
    var chain = $"aresample={outputRate},aformat=sample_fmts=fltp:sample_rates={outputRate}:channel_layouts=mono";
    var graph = string.Join(";", inputs.Select((_, index) => $"[{index}:a]{chain}[a{index}]")) + ";" + string.Concat(inputs.Select((_, index) => $"[a{index}]")) + $"concat=n={inputs.Length}:v=0:a=1[aout]";
    Assert.Equal(expectedInputs, inputs.Length);
    var result = await Run(tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-y", .. inputs.SelectMany(input => new[] { "-i", input }), "-filter_complex", graph, "-map", "[aout]", "-f", "f32le", raw]);
    Assert.Equal(0, result.ExitCode);
    var length = new FileInfo(raw).Length / 4;
    if (keep is null) File.Delete(raw);
    return length;
  }

  private static float[] ReadFloats(string path)
  {
    var bytes = File.ReadAllBytes(path);
    var values = new float[bytes.Length / 4];
    Buffer.BlockCopy(bytes, 0, values, 0, values.Length * 4);
    return values;
  }

  private static double Tone(float[] samples, long start, int frequency, int outputRate)
  {
    double real = 0, imaginary = 0;
    for (var index = 0; index < 4096; index++)
    {
      var angle = 2 * Math.PI * frequency * index / outputRate;
      real += samples[start + index] * Math.Cos(angle);
      imaginary += samples[start + index] * Math.Sin(angle);
    }
    return real * real + imaginary * imaginary;
  }

  private static double SignalToNoise(float[] output, float[] reference, long start, int length)
  {
    double signal = 0, noise = 0;
    for (var index = start; index < start + length; index++)
    {
      signal += reference[index] * (double)reference[index];
      noise += Math.Pow(output[index] - reference[index], 2);
    }
    return 10 * Math.Log10(signal / Math.Max(noise, 1e-20));
  }

  private static Task<ProcessResult> Run(string executable, IEnumerable<string> arguments) => new ProcessRunner().RunAsync(new ProcessSpec(executable, arguments.ToArray()), null, CancellationToken.None);

  private static MediaToolSet ResolveTools()
  {
    var directory = Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR");
    if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "ffmpeg.exe"))) throw Xunit.Sdk.SkipException.ForSkip("Pinned FFmpeg tools are not available; set BOOKSPLICE_FFMPEG_DIR to run real-media integration tests.");
    return new MediaToolLocator(directory).Resolve();
  }

  private static async Task<(string Type, string TimeBase)[]> ProbeStreamsAsync(MediaToolSet tools, string path)
  {
    var result = await Run(tools.FFprobePath, ["-v", "error", "-show_entries", "stream=index,codec_type,time_base", "-of", "json", path]);
    Assert.Equal(0, result.ExitCode);
    using var document = JsonDocument.Parse(result.StandardOutput);
    return document.RootElement.GetProperty("streams").EnumerateArray().Select(stream => (stream.GetProperty("codec_type").GetString()!, stream.GetProperty("time_base").GetString()!)).ToArray();
  }

  private sealed class FixedSpace : IStorageSpaceProvider { public long? GetAvailableBytes(string destinationDirectory) => long.MaxValue; }
}
