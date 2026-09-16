using System.Text;
using System.Text.Json;
using AudiobookConverter.Core.Analysis;
using AudiobookConverter.FFmpeg.Execution;

namespace AudiobookConverter.FFmpeg.Tests.Execution;

public sealed class JsonJobLogWriterTests
{
  [Fact]
  public async Task WritePublishesOneUtf8RecordAndReplacesTheSameJob()
  {
    using var directory = new TemporaryDirectory();
    var writer = new JsonJobLogWriter(directory.Path);
    var jobId = Guid.Parse("be62f293-f6c8-4cd0-92e6-55d9a0dc76f7");

    await writer.WriteAsync(Audit(jobId, ConversionTerminalStatus.ExecutionFailed, "first"), CancellationToken.None);
    await writer.WriteAsync(Audit(jobId, ConversionTerminalStatus.Succeeded, "second"), CancellationToken.None);

    var file = Assert.Single(Directory.GetFiles(directory.Path));
    Assert.Equal($"{jobId:D}.json", Path.GetFileName(file));
    var bytes = File.ReadAllBytes(file);
    Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
    using var json = JsonDocument.Parse(bytes);
    Assert.Equal("Succeeded", json.RootElement.GetProperty("terminalStatus").GetString());
    Assert.Equal("second", json.RootElement.GetProperty("diagnostics")[0].GetProperty("message").GetString());
  }

  [Fact]
  public async Task WriteKeepsExactPathsOnlyInOrderedSourcesAndSanitizesHostileDiagnostics()
  {
    using var directory = new TemporaryDirectory();
    var jobId = Guid.NewGuid();
    var source = "C:\\Private Books\\Book One\\01.mp3";
    var diagnostic = "bad\u0001 C:\\Private Books\\Secret Title\\one.mp3 \\\\server\\share\\Private Books\\Secret Title\\two.mp3 /private books/secret title/three.mp3 /secret";
    var record = Audit(jobId, ConversionTerminalStatus.Cancelled, diagnostic) with { OrderedSources = [source] };

    await new JsonJobLogWriter(directory.Path).WriteAsync(record, CancellationToken.None);

    using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory.Path, $"{jobId:D}.json")));
    Assert.Equal(source, json.RootElement.GetProperty("orderedSources")[0].GetString());
    var message = json.RootElement.GetProperty("diagnostics")[0].GetProperty("message").GetString()!;
    Assert.DoesNotContain("C:\\", message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("\\\\server", message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Private Books", message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Secret Title", message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("/private", message, StringComparison.Ordinal);
    Assert.DoesNotContain("/secret", message, StringComparison.Ordinal);
    Assert.DoesNotContain('\u0001', message);
    Assert.Equal("Cancelled", json.RootElement.GetProperty("terminalStatus").GetString());
  }

  [Fact]
  public async Task FailedPublicationRemovesTheOwnedPartial()
  {
    using var directory = new TemporaryDirectory();
    var operations = new FailingMoveOperations();
    var writer = new JsonJobLogWriter(directory.Path, operations);

    await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(Audit(Guid.NewGuid(), ConversionTerminalStatus.ValidationFailed, "failed"), CancellationToken.None));

    Assert.Empty(Directory.GetFiles(directory.Path));
    Assert.NotNull(operations.PartialPath);
    Assert.Equal(directory.Path, Path.GetDirectoryName(operations.PartialPath), ignoreCase: true);
  }

  [Fact]
  public async Task WriteRecordsSafelyQuotedCommandEvidenceWithoutRepeatingSourcePaths()
  {
    using var directory = new TemporaryDirectory();
    var jobId = Guid.NewGuid();
    var source = "C:\\Private Books\\one.mp3";
    var execution = new ConversionExecutionResult(ExecutionStatus.Failed, null,
      [new ExecutionProcessResult(1, "", "", null, "C:\\Tools\\ffmpeg.exe", ["-i", source, "-metadata", "title=Two Words"])], []);
    var record = Audit(jobId, ConversionTerminalStatus.ExecutionFailed, "failed") with { OrderedSources = [source], Execution = execution };

    await new JsonJobLogWriter(directory.Path).WriteAsync(record, CancellationToken.None);

    using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory.Path, $"{jobId:D}.json")));
    var evidence = json.RootElement.GetProperty("commandEvidence")[0].GetString()!;
    Assert.Contains("\"title=Two Words\"", evidence, StringComparison.Ordinal);
    Assert.DoesNotContain(source, evidence, StringComparison.OrdinalIgnoreCase);
  }

  private static ConversionAuditRecord Audit(Guid jobId, ConversionTerminalStatus status, string message) => new(
    ConversionAuditRecord.CurrentSchemaVersion,
    jobId,
    DateTimeOffset.UnixEpoch,
    DateTimeOffset.UnixEpoch.AddSeconds(1),
    status,
    null,
    null,
    null,
    null,
    null,
    [],
    null,
    null,
    null,
    null,
    [],
    [new("test", ServiceDiagnosticSeverity.Error, message)],
    null);

  private sealed class FailingMoveOperations : IJobLogFileOperations
  {
    public string? PartialPath { get; private set; }
    public Stream Create(string path)
    {
      PartialPath = path;
      return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }
    public bool Exists(string path) => File.Exists(path);
    public void Replace(string source, string destination) => throw new IOException("injected");
    public void Move(string source, string destination) => throw new IOException("injected");
    public void Delete(string path) => File.Delete(path);
  }

  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"abc-log-{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }
    public string Path { get; }
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
  }
}
