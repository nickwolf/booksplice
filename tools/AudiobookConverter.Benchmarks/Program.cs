using AudiobookConverter.FFmpeg.Execution;
using AudiobookConverter.FFmpeg.Probing;
using AudiobookConverter.FFmpeg.Tools;

namespace AudiobookConverter.Benchmarks;

public static class Program
{
  public static async Task<int> Main(string[] args)
  {
    var corpusPath = Value(args, "--corpus"); var outputPath = Value(args, "--output"); var mediaOutput = Value(args, "--media-output");
    if (corpusPath is null || outputPath is null) throw new ArgumentException("--corpus and --output are required.");
    var toolDir = Value(args, "--tools") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "tools", "ffmpeg");
    var cases = CorpusManifestLoader.Load(corpusPath);
    var tools = new MediaToolLocator(toolDir).Resolve();
    var runner = new BenchmarkRunner(new ProcessRunner(), new FFprobeMediaProbe(new ProcessRunner(), tools), tools);
    var options = new BenchmarkRunOptions(Value(args, "--strategy") ?? "direct-concat-transcode", Int(args, "--bitrate", 128), Value(args, "--channel-mode") ?? "preserve", Value(args, "--validation") ?? "lightweight", Int(args, "--concurrency", 1), Value(args, "--storage-class") ?? "private-copy-to-local", Value(args, "--operation") ?? "", Value(args, "--round-id") ?? "");
    var columns = new List<string[]>();
    foreach (var item in cases)
    {
      var mediaDirectory = mediaOutput ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, "media");
      Directory.CreateDirectory(mediaDirectory);
      var output = Path.Combine(mediaDirectory, item.CaseId + "-" + Guid.NewGuid().ToString("N") + ".m4a");
      BenchmarkResult result;
      try { result = await runner.RunAsync(item, output, options).ConfigureAwait(false); }
      catch { result = new BenchmarkResult(BenchmarkResult.NewRunId(), item.CaseId, null, null, null, null, null, null, null, null, null, null, null, null, null, "failed", "benchmark processing failed"); }
      var row = result.ToColumns();
      if (BenchmarkResultValidator.Validate(CsvBenchmarkWriter.Header.Split(','), row).Count > 0) throw new InvalidDataException("benchmark result failed validation");
      columns.Add(row);
      CsvBenchmarkWriter.Append(outputPath, result);
    }
    if (BenchmarkResultValidator.ValidateCollection(CsvBenchmarkWriter.Header.Split(','), columns).Count > 0) throw new InvalidDataException("benchmark results failed collection validation");
    return 0;
  }

  private static string? Value(string[] args, string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
  private static int Int(string[] args, string name, int fallback) => int.TryParse(Value(args, name), out var value) ? value : fallback;
}
