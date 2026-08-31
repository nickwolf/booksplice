using AudiobookConverter.FFmpeg.Execution;
using AudiobookConverter.FFmpeg.Probing;
using AudiobookConverter.FFmpeg.Tools;

namespace AudiobookConverter.Benchmarks;

public static class Program
{
  public static async Task<int> Main(string[] args)
  {
    var corpusPath = Value(args, "--corpus"); var outputPath = Value(args, "--output");
    if (corpusPath is null || outputPath is null) throw new ArgumentException("--corpus and --output are required.");
    var toolDir = Value(args, "--tools") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "tools", "ffmpeg");
    var cases = CorpusManifestLoader.Load(corpusPath);
    var tools = new MediaToolLocator(toolDir).Resolve();
    var runner = new BenchmarkRunner(new ProcessRunner(), new FFprobeMediaProbe(new ProcessRunner(), tools), tools);
    foreach (var item in cases)
    {
      var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, item.CaseId + ".m4a");
      BenchmarkResult result;
      try { result = await runner.RunAsync(item, output).ConfigureAwait(false); }
      catch { result = new BenchmarkResult(BenchmarkResult.NewRunId(), item.CaseId, null, null, null, null, null, null, null, null, null, null, null, null, null, "failed", "benchmark processing failed"); }
      CsvBenchmarkWriter.Append(outputPath, result);
    }
    return 0;
  }

  private static string? Value(string[] args, string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
}
