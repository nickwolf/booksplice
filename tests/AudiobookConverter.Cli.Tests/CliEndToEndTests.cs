using System.Security.Cryptography;
using System.Text.Json;
using AudiobookConverter.Cli;
using AudiobookConverter.FFmpeg.Execution;
using AudiobookConverter.FFmpeg.Probing;
using AudiobookConverter.FFmpeg.Tools;

namespace AudiobookConverter.Cli.Tests;

public sealed class CliEndToEndTests
{
  [PinnedCliMediaFact]
  public async Task GeneratedMultiMp3WithExternalCoverUnicodeAndNoChaptersConvertsWithoutChangingSources()
  {
    var tools = ResolveTools();
    using var root = new TemporaryDirectory();
    var source = Directory.CreateDirectory(Path.Combine(root.Path, "Book with spaces 日本語")).FullName;
    var first = await GenerateAudioAsync(tools, Path.Combine(source, "01 first.mp3"), "libmp3lame", "440", "1");
    var second = await GenerateAudioAsync(tools, Path.Combine(source, "02 second.mp3"), "libmp3lame", "550", "2");
    await GenerateImageAsync(tools, Path.Combine(source, "cover.jpg"));
    var before = Hashes(first, second);
    var outputDirectory = Directory.CreateDirectory(Path.Combine(root.Path, "output")).FullName;
    var localData = Path.Combine(root.Path, "local");
    var stdout = new StringWriter();
    var stderr = new StringWriter();

    var exitCode = await CliComposition.Create(localData, ToolDirectory()).RunAsync(
      [source, "--output", outputDirectory, "--no-chapters", "--json"], stdout, stderr, CancellationToken.None);

    var logs = Path.Combine(localData, "AudiobookConverter", "logs");
    var failure = Directory.Exists(logs) ? string.Join(Environment.NewLine, Directory.GetFiles(logs).Select(File.ReadAllText)) : string.Empty;
    Assert.True(exitCode == CliExitCode.Success, stderr + Environment.NewLine + failure);
    Assert.All(stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), line => JsonDocument.Parse(line).Dispose());
    var output = Assert.Single(Directory.GetFiles(outputDirectory, "*.m4b"));
    Assert.Equal(before, Hashes(first, second));
    var media = await new FFprobeMediaProbe(new ProcessRunner(), tools).ProbeAsync(output);
    Assert.Single(media.AudioStreams);
    Assert.Empty(media.Chapters);
    Assert.Single(media.AttachedPictures);
    Assert.Single(Directory.GetFiles(Path.Combine(localData, "AudiobookConverter", "logs"), "*.json"));
  }

  [PinnedCliMediaFact]
  public async Task CompatibleAacWithoutCoverUsesCollisionSafePublicationAndPreservesSources()
  {
    var tools = ResolveTools();
    using var root = new TemporaryDirectory();
    var source = Directory.CreateDirectory(Path.Combine(root.Path, "AAC Book")).FullName;
    var first = await GenerateAudioAsync(tools, Path.Combine(source, "01.m4a"), "aac", "440", "1");
    var second = await GenerateAudioAsync(tools, Path.Combine(source, "02.m4a"), "aac", "550", "2");
    var before = Hashes(first, second);
    var outputDirectory = Directory.CreateDirectory(Path.Combine(root.Path, "output")).FullName;
    var application = CliComposition.Create(Path.Combine(root.Path, "local"), ToolDirectory());

    var firstExit = await application.RunAsync([source, "--output", outputDirectory], TextWriter.Null, TextWriter.Null, CancellationToken.None);
    var secondExit = await application.RunAsync([source, "--output", outputDirectory], TextWriter.Null, TextWriter.Null, CancellationToken.None);

    Assert.Equal(CliExitCode.Success, firstExit);
    Assert.Equal(CliExitCode.Success, secondExit);
    Assert.Equal(2, Directory.GetFiles(outputDirectory, "*.m4b").Length);
    Assert.Equal(before, Hashes(first, second));
    foreach (var output in Directory.GetFiles(outputDirectory, "*.m4b"))
    {
      var media = await new FFprobeMediaProbe(new ProcessRunner(), tools).ProbeAsync(output);
      Assert.Single(media.AudioStreams);
      Assert.Empty(media.AttachedPictures);
    }
  }

  [PinnedCliMediaFact]
  public async Task DryRunCreatesNoOutputTemporaryOrAuditFiles()
  {
    var tools = ResolveTools();
    using var root = new TemporaryDirectory();
    var source = Directory.CreateDirectory(Path.Combine(root.Path, "Dry Book")).FullName;
    await GenerateAudioAsync(tools, Path.Combine(source, "01.mp3"), "libmp3lame", "440", "1");
    var outputDirectory = Directory.CreateDirectory(Path.Combine(root.Path, "output")).FullName;
    var localData = Path.Combine(root.Path, "local");

    var exitCode = await CliComposition.Create(localData, ToolDirectory()).RunAsync(
      [source, "--output", outputDirectory, "--dry-run", "--json"], TextWriter.Null, TextWriter.Null, CancellationToken.None);

    Assert.Equal(CliExitCode.Success, exitCode);
    Assert.Empty(Directory.GetFiles(outputDirectory));
    Assert.False(Directory.Exists(Path.Combine(localData, "AudiobookConverter")));
  }

  [PinnedCliMediaFact]
  public async Task CorruptAndAmbiguousInputsReturnDistinctCodesWithoutFinalOutputs()
  {
    var tools = ResolveTools();
    using var root = new TemporaryDirectory();
    var outputDirectory = Directory.CreateDirectory(Path.Combine(root.Path, "output")).FullName;
    var corrupt = Directory.CreateDirectory(Path.Combine(root.Path, "Corrupt")).FullName;
    await File.WriteAllTextAsync(Path.Combine(corrupt, "01.mp3"), "not media");
    var ambiguous = Directory.CreateDirectory(Path.Combine(root.Path, "Ambiguous")).FullName;
    await GenerateAudioAsync(tools, Path.Combine(ambiguous, "01.mp3"), "libmp3lame", "440", "2");
    await GenerateAudioAsync(tools, Path.Combine(ambiguous, "02.mp3"), "libmp3lame", "550", "1");
    var application = CliComposition.Create(Path.Combine(root.Path, "local"), ToolDirectory());

    var corruptExit = await application.RunAsync([corrupt, "--output", outputDirectory], TextWriter.Null, TextWriter.Null, CancellationToken.None);
    var ambiguousExit = await application.RunAsync([ambiguous, "--output", outputDirectory], TextWriter.Null, TextWriter.Null, CancellationToken.None);

    Assert.Equal(CliExitCode.InvalidInput, corruptExit);
    Assert.Equal(CliExitCode.OrderingDecisionRequired, ambiguousExit);
    Assert.Empty(Directory.GetFiles(outputDirectory, "*.m4b"));
  }

  [PinnedCliMediaFact]
  public async Task PreCancelledCommandReturnsCancellationWithoutFinalOutput()
  {
    var tools = ResolveTools();
    using var root = new TemporaryDirectory();
    var source = Directory.CreateDirectory(Path.Combine(root.Path, "Cancelled")).FullName;
    await GenerateAudioAsync(tools, Path.Combine(source, "01.mp3"), "libmp3lame", "440", "1");
    var outputDirectory = Directory.CreateDirectory(Path.Combine(root.Path, "output")).FullName;
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    var exitCode = await CliComposition.Create(Path.Combine(root.Path, "local"), ToolDirectory()).RunAsync(
      [source, "--output", outputDirectory], TextWriter.Null, TextWriter.Null, cancellation.Token);

    Assert.Equal(CliExitCode.Cancelled, exitCode);
    Assert.Empty(Directory.GetFiles(outputDirectory, "*.m4b"));
  }

  private static async Task<string> GenerateAudioAsync(MediaToolSet tools, string path, string codec, string frequency, string track)
  {
    var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency={frequency}:duration=1", "-ac", "1", "-ar", "44100", "-c:a", codec };
    if (codec == "aac") arguments.AddRange(["-b:a", "96k"]);
    arguments.AddRange(["-metadata", $"track={track}", path]);
    var result = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFmpegPath, arguments), null, CancellationToken.None);
    Assert.Equal(0, result.ExitCode);
    return path;
  }

  private static async Task GenerateImageAsync(MediaToolSet tools, string path)
  {
    var result = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=32x32:d=1", "-frames:v", "1", "-c:v", "mjpeg", path]), null, CancellationToken.None);
    Assert.Equal(0, result.ExitCode);
  }

  private static string[] Hashes(params string[] paths) => paths.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
  private static string ToolDirectory() => Environment.GetEnvironmentVariable("AUDIOBOOKCONVERTER_FFMPEG_DIR")!;
  private static MediaToolSet ResolveTools() => new MediaToolLocator(ToolDirectory()).Resolve();

  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("audiobookconverter-cli-").FullName;
    public string Path { get; }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
  }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class PinnedCliMediaFactAttribute : FactAttribute
{
  public PinnedCliMediaFactAttribute()
  {
    var directory = Environment.GetEnvironmentVariable("AUDIOBOOKCONVERTER_FFMPEG_DIR");
    if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "ffmpeg.exe")))
      Skip = "Pinned FFmpeg tools are unavailable.";
  }
}
