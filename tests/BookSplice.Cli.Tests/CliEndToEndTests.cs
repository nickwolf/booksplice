using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using BookSplice.Cli;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.Cli.Tests;

public sealed class CliEndToEndTests
{
  [Fact]
  public async Task MissingMediaToolsReturnExecutionFailureWithFinalJsonAndAudit()
  {
    using var root = new TemporaryDirectory();
    var outputDirectory = Directory.CreateDirectory(Path.Combine(root.Path, "output")).FullName;
    var localData = Path.Combine(root.Path, "local");
    var missingTools = Directory.CreateDirectory(Path.Combine(root.Path, "missing-tools")).FullName;
    var stdout = new StringWriter();

    var exitCode = await CliComposition.Create(localData, missingTools).RunAsync(
      ["book", "--output", outputDirectory, "--json"], stdout, TextWriter.Null, CancellationToken.None);

    Assert.Equal(CliExitCode.ExecutionFailure, exitCode);
    var finalLine = stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[^1];
    using var final = JsonDocument.Parse(finalLine);
    Assert.Equal("ExecutionFailed", final.RootElement.GetProperty("status").GetString());
    var audit = Assert.Single(Directory.GetFiles(Path.Combine(localData, "BookSplice", "logs"), "*.json"));
    using var auditJson = JsonDocument.Parse(File.ReadAllBytes(audit));
    Assert.Equal("ExecutionFailed", auditJson.RootElement.GetProperty("terminalStatus").GetString());
  }

  [Fact]
  public async Task PreCancelledCommandWithMissingMediaToolsReturnsCancellationWithFinalJsonAndAudit()
  {
    using var root = new TemporaryDirectory();
    var outputDirectory = Directory.CreateDirectory(Path.Combine(root.Path, "output")).FullName;
    var localData = Path.Combine(root.Path, "local");
    var missingTools = Directory.CreateDirectory(Path.Combine(root.Path, "missing-tools")).FullName;
    var stdout = new StringWriter();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    var exitCode = await CliComposition.Create(localData, missingTools).RunAsync(
      ["book", "--output", outputDirectory, "--json"], stdout, TextWriter.Null, cancellation.Token);

    Assert.Equal(CliExitCode.Cancelled, exitCode);
    var finalLine = stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[^1];
    using var final = JsonDocument.Parse(finalLine);
    Assert.Equal("Cancelled", final.RootElement.GetProperty("status").GetString());
    var audit = Assert.Single(Directory.GetFiles(Path.Combine(localData, "BookSplice", "logs"), "*.json"));
    using var auditJson = JsonDocument.Parse(File.ReadAllBytes(audit));
    Assert.Equal("Cancelled", auditJson.RootElement.GetProperty("terminalStatus").GetString());
  }

  [Fact]
  public async Task PreServiceJsonFailuresWriteOneFinalEventAndAudit()
  {
    using var root = new TemporaryDirectory();
    var missingTools = Directory.CreateDirectory(Path.Combine(root.Path, "missing-tools")).FullName;
    var invalidLocalData = Path.Combine(root.Path, "invalid-settings");
    var invalidOutput = new StringWriter();

    var invalidExit = await CliComposition.Create(invalidLocalData, missingTools).RunAsync(
      ["book", "--json"], invalidOutput, TextWriter.Null, CancellationToken.None);

    Assert.Equal(CliExitCode.InvalidInput, invalidExit);
    AssertFinalAndAudit(invalidOutput, invalidLocalData, "InvalidInput", CliExitCode.InvalidInput);

    var usageLocalData = Path.Combine(root.Path, "usage-error");
    var usageOutput = new StringWriter();
    var usageExit = await CliComposition.Create(usageLocalData, missingTools).RunAsync(
      ["book", "--unknown", "--json"], usageOutput, TextWriter.Null, CancellationToken.None);

    Assert.Equal(CliExitCode.UsageError, usageExit);
    AssertFinalAndAudit(usageOutput, usageLocalData, "InvalidInput", CliExitCode.UsageError);
  }

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

    var logs = Path.Combine(localData, "BookSplice", "logs");
    var failure = Directory.Exists(logs) ? string.Join(Environment.NewLine, Directory.GetFiles(logs).Select(File.ReadAllText)) : string.Empty;
    Assert.True(exitCode == CliExitCode.Success, stderr + Environment.NewLine + failure);
    Assert.All(stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), line => JsonDocument.Parse(line).Dispose());
    var output = Assert.Single(Directory.GetFiles(outputDirectory, "*.m4b"));
    Assert.Equal(before, Hashes(first, second));
    var media = await new FFprobeMediaProbe(new ProcessRunner(), tools).ProbeAsync(output);
    Assert.Single(media.AudioStreams);
    Assert.Empty(media.Chapters);
    Assert.Single(media.AttachedPictures);
    Assert.Single(Directory.GetFiles(Path.Combine(localData, "BookSplice", "logs"), "*.json"));
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
    var localData = Path.Combine(root.Path, "local");
    var application = CliComposition.Create(localData, ToolDirectory());

    var firstExit = await application.RunAsync([source, "--output", outputDirectory], TextWriter.Null, TextWriter.Null, CancellationToken.None);
    var secondExit = await application.RunAsync([source, "--output", outputDirectory], TextWriter.Null, TextWriter.Null, CancellationToken.None);

    Assert.Equal(CliExitCode.Success, firstExit);
    Assert.Equal(CliExitCode.Success, secondExit);
    Assert.Equal(2, Directory.GetFiles(outputDirectory, "*.m4b").Length);
    Assert.Equal(before, Hashes(first, second));
    foreach (var audit in Directory.GetFiles(Path.Combine(localData, "BookSplice", "logs"), "*.json"))
    {
      using var auditJson = JsonDocument.Parse(File.ReadAllBytes(audit));
      Assert.Equal("AacStreamCopy", auditJson.RootElement.GetProperty("plan").GetProperty("strategy").GetString());
      Assert.Contains("-c:a copy", auditJson.RootElement.GetProperty("commandEvidence")[0].GetString(), StringComparison.Ordinal);
    }
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
    Assert.False(Directory.Exists(Path.Combine(localData, "BookSplice")));
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

  [PinnedCliMediaFact]
  public async Task CancellingAfterFfmpegStartsTerminatesConversionAndAuditsWithoutPublishing()
  {
    var tools = ResolveTools();
    using var root = new TemporaryDirectory();
    var source = Directory.CreateDirectory(Path.Combine(root.Path, "Long running book")).FullName;
    var input = await GenerateAudioAsync(tools, Path.Combine(source, "01.mp3"), "libmp3lame", "440", "1", "600");
    var before = Hashes(input);
    var outputDirectory = Directory.CreateDirectory(Path.Combine(root.Path, "output")).FullName;
    var localData = Path.Combine(root.Path, "local");
    using var cancellation = new CancellationTokenSource();
    var childId = 0;
    ProcessRunner.ProcessStarted.Value = id => childId = id;
    var conversion = CliComposition.Create(localData, ToolDirectory()).RunAsync([source, "--output", outputDirectory], TextWriter.Null, TextWriter.Null, cancellation.Token);
    Process? child = null;
    try
    {
      await WaitForOutputAsync(Path.Combine(localData, "BookSplice", "temp"));
      Assert.True(childId > 0);
      child = Process.GetProcessById(childId);
      cancellation.Cancel();
      var exitCode = await conversion;

      Assert.Equal(CliExitCode.Cancelled, exitCode);
      Assert.Empty(Directory.GetFiles(outputDirectory, "*.m4b"));
      Assert.Equal(before, Hashes(input));
      var audit = Assert.Single(Directory.GetFiles(Path.Combine(localData, "BookSplice", "logs"), "*.json"));
      using var auditJson = JsonDocument.Parse(File.ReadAllBytes(audit));
      Assert.Equal("Cancelled", auditJson.RootElement.GetProperty("terminalStatus").GetString());
      await WaitForExitAsync(child);
    }
    finally
    {
      cancellation.Cancel();
      try { await conversion; } catch (OperationCanceledException) { }
      ProcessRunner.ProcessStarted.Value = null;
      child?.Dispose();
    }
  }

  private static async Task<string> GenerateAudioAsync(MediaToolSet tools, string path, string codec, string frequency, string track, string duration = "1")
  {
    var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency={frequency}:duration={duration}", "-ac", "1", "-ar", "44100", "-c:a", codec };
    if (codec == "aac") arguments.AddRange(["-b:a", "96k"]);
    arguments.AddRange(["-metadata", $"track={track}", path]);
    var generator = codec == "libmp3lame" ? FixtureGeneratorPath() : tools.FFmpegPath;
    var result = await new ProcessRunner().RunAsync(new ProcessSpec(generator, arguments), null, CancellationToken.None);
    Assert.Equal(0, result.ExitCode);
    return path;
  }

  private static async Task GenerateImageAsync(MediaToolSet tools, string path)
  {
    var result = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=32x32:d=1", "-frames:v", "1", "-c:v", "mjpeg", path]), null, CancellationToken.None);
    Assert.Equal(0, result.ExitCode);
  }

  private static string[] Hashes(params string[] paths) => paths.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
  private static void AssertFinalAndAudit(StringWriter output, string localData, string status, CliExitCode exitCode)
  {
    var finalLine = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[^1];
    using var final = JsonDocument.Parse(finalLine);
    Assert.Equal("final", final.RootElement.GetProperty("event").GetString());
    Assert.Equal(status, final.RootElement.GetProperty("status").GetString());
    Assert.Equal((int)exitCode, final.RootElement.GetProperty("exitCode").GetInt32());
    var audit = Assert.Single(Directory.GetFiles(Path.Combine(localData, "BookSplice", "logs"), "*.json"));
    using var auditJson = JsonDocument.Parse(File.ReadAllBytes(audit));
    Assert.Equal(status, auditJson.RootElement.GetProperty("terminalStatus").GetString());
  }
  private static async Task WaitForOutputAsync(string temporaryRoot)
  {
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (DateTime.UtcNow < deadline)
    {
      if (Directory.Exists(temporaryRoot) && Directory.EnumerateFiles(temporaryRoot, "output.m4b", SearchOption.AllDirectories).Any(path => new FileInfo(path).Length > 0)) return;
      await Task.Delay(25);
    }
    throw new TimeoutException("FFmpeg did not begin writing the temporary output.");
  }
  private static async Task WaitForExitAsync(Process process)
  {
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline)
    {
      if (process.HasExited) return;
      await Task.Delay(25);
    }
    Assert.True(process.HasExited);
  }
  private static string ToolDirectory() => Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR")!;
  private static MediaToolSet ResolveTools() => new MediaToolLocator(ToolDirectory()).Resolve();
  private static string FixtureGeneratorPath()
  {
    var directory = Environment.GetEnvironmentVariable("BOOKSPLICE_FIXTURE_FFMPEG_DIR") ?? ToolDirectory();
    return new MediaToolLocator(directory).Resolve().FFmpegPath;
  }

  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("booksplice-cli-").FullName;
    public string Path { get; }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
  }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class PinnedCliMediaFactAttribute : FactAttribute
{
  public PinnedCliMediaFactAttribute()
  {
    var directory = Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR");
    if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "ffmpeg.exe")))
      Skip = "Pinned FFmpeg tools are unavailable.";
  }
}
