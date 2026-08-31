using System.Text.Json;
using AudiobookConverter.FFmpeg.Execution;
using AudiobookConverter.FFmpeg.Probing;
using AudiobookConverter.FFmpeg.Tools;

namespace AudiobookConverter.Benchmarks;

public static class Program
{
  private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
  public static async Task<int> Main(string[] args)
  {
    string? corpusPath = Value(args, "--corpus"), outputPath = Value(args, "--output");
    if (corpusPath is null || outputPath is null) throw new ArgumentException("--corpus and --output are required.");
    var toolDir = Value(args, "--tools") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "tools", "ffmpeg");
    var document = JsonSerializer.Deserialize<Corpus>(await File.ReadAllTextAsync(corpusPath), JsonOptions) ?? throw new InvalidDataException("Invalid corpus.");
    if (document.Cases is null || document.Cases.Count == 0 || document.Cases.Any(c => string.IsNullOrWhiteSpace(c.CaseId) || string.IsNullOrWhiteSpace(c.SourcePath) || c.Traits is null || c.ExpectedOrder is null || c.Groups is null) || document.Cases.Select(c => c.CaseId).Distinct(StringComparer.Ordinal).Count() != document.Cases.Count) throw new InvalidDataException("Corpus contains missing, empty, or duplicate anonymous case IDs.");
    var tools = new MediaToolLocator(toolDir).Resolve();
    var runner = new BenchmarkRunner(new ProcessRunner(), new FFprobeMediaProbe(new ProcessRunner(), tools), tools);
    foreach (var item in document.Cases)
    {
      var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, item.CaseId + ".m4a");
      var result = await runner.RunAsync(new BenchmarkCase(item.CaseId, item.SourcePath, item.Traits, item.ExpectedOrder, item.Groups, item.CopyPermission, item.ExpectedProperties, item.ExpectedCorrupt), output);
      CsvBenchmarkWriter.Append(outputPath, result);
    }
    return 0;
  }
  private static string? Value(string[] args, string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
  private sealed record Corpus(string SchemaVersion, string? GeneratedBy, List<CorpusCase> Cases);
  private sealed record CorpusCase(string CaseId, string SourcePath, List<string> Traits, List<string> ExpectedOrder, List<string> Groups, bool CopyPermission, Dictionary<string,string>? ExpectedProperties = null, bool ExpectedCorrupt = false, List<string>? Arguments = null);
}
