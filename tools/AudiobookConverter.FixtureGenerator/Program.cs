using System.Text.Json;
using AudiobookConverter.FFmpeg.Execution;
using AudiobookConverter.FFmpeg.Tools;

namespace AudiobookConverter.FixtureGenerator;

public static class Program
{
  private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
  private static readonly string[] SmokeGroup = ["smoke"];
  public static async Task<int> Main(string[] args)
  {
    var root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "fixtures");
    var toolsDir = args.Length > 1 ? args[1] : Path.Combine(Environment.CurrentDirectory, "artifacts", "tools", "ffmpeg");
    var tools = new MediaToolLocator(toolsDir).Resolve();
    var runner = new ProcessRunner();
    Directory.CreateDirectory(root);
    var definitions = FixtureCatalog.Cases;
    foreach (var definition in definitions)
    {
      var output = Path.Combine(root, definition.RelativePath);
      Directory.CreateDirectory(Path.GetDirectoryName(output)!);
      var result = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-y", .. definition.Arguments, output]), null, CancellationToken.None);
      if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
    }
    var sourceForCorrupt = Path.Combine(root, "mp3/mono-096.mp3");
    var corrupt = Path.Combine(root, "corrupt/truncated.mp3");
    Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!);
    var bytes = await File.ReadAllBytesAsync(sourceForCorrupt);
    await File.WriteAllBytesAsync(corrupt, bytes[..Math.Max(1, bytes.Length / 3)]);
    if (args.Contains("--long", StringComparer.Ordinal))
    {
      var longPath = Path.Combine(root, "long/long-60s.wav");
      Directory.CreateDirectory(Path.GetDirectoryName(longPath)!);
      var longResult = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=60", "-c:a", "pcm_s16le", longPath]), null, CancellationToken.None);
      if (longResult.ExitCode != 0) throw new InvalidOperationException(longResult.StandardError);
    }
    var manifest = new { schemaVersion = "1", generatedBy = "AudiobookConverter.FixtureGenerator", cases = definitions.Select(d => new { caseId = d.CaseId, sourcePath = Path.Combine(root, d.RelativePath), traits = d.Traits, expectedOrder = new[] { Path.GetFileName(d.RelativePath) }, groups = SmokeGroup, copyPermission = true, arguments = d.Arguments, expectedProperties = d.ExpectedProperties }) };
    await File.WriteAllTextAsync(Path.Combine(root, "corpus.json"), JsonSerializer.Serialize(manifest, JsonOptions));
    return 0;
  }
}
