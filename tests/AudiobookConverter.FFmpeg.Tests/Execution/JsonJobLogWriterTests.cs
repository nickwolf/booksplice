using System.Text;
using System.Text.Json;
using AudiobookConverter.Core.Analysis;
using AudiobookConverter.Core.Chapters;
using AudiobookConverter.Core.Covers;
using AudiobookConverter.Core.Discovery;
using AudiobookConverter.Core.Metadata;
using AudiobookConverter.Core.Ordering;
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

  [Fact]
  public async Task WriteRecordsLabeledOrderEvidenceAndProbeSummariesWithoutSourcePaths()
  {
    using var directory = new TemporaryDirectory();
    var jobId = Guid.NewGuid();
    var firstPath = "C:\\Private Books\\Secret Title\\01 - Secret Chapter.m4a";
    var secondPath = "C:\\Private Books\\Secret Title\\02 - Private Ending.m4a";
    var first = Source(firstPath, "01 - Secret Chapter.m4a", "aac", "LC", 48000, 2, 4.5m, "mov,mp4,m4a", 1234);
    var second = Source(secondPath, "02 - Private Ending.m4a", "aac", "LC", 48000, 2, 5.5m, "mov,mp4,m4a", 2345);
    var selected = new OrderCandidate(OrderCandidateId.Metadata, [first.RelativePath, second.RelativePath], true, OrderConfidence.High, [new OrderEvidence("order.metadata.track", [first.RelativePath, second.RelativePath])]);
    var resolution = new OrderResolution(OrderStatus.Resolved, selected, [selected], [new OrderEvidence("order.metadata.selected", [first.RelativePath, second.RelativePath])]);
    var analysis = new BookAnalysis(BookAnalysisStatus.Ready, "C:\\Private Books", "Audiobook", [first, second], resolution, EmptyMetadata(), new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([], 10_000_000), []);
    var record = Audit(jobId, ConversionTerminalStatus.Succeeded, "done") with { Analysis = analysis, OrderedSources = [firstPath, secondPath] };

    await new JsonJobLogWriter(directory.Path).WriteAsync(record, CancellationToken.None);

    using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory.Path, $"{jobId:D}.json")));
    var analysisJson = json.RootElement.GetProperty("analysis");
    var order = analysisJson.GetProperty("orderResolution");
    Assert.Equal("Resolved", order.GetProperty("status").GetString());
    Assert.Equal("Metadata", order.GetProperty("selectedCandidate").GetProperty("id").GetString());
    Assert.Equal(["source-1", "source-2"], order.GetProperty("selectedCandidate").GetProperty("orderedSources").EnumerateArray().Select(value => value.GetString()));
    Assert.Equal("order.metadata.selected", order.GetProperty("evidence")[0].GetProperty("code").GetString());
    Assert.Equal(["source-1", "source-2"], order.GetProperty("evidence")[0].GetProperty("sources").EnumerateArray().Select(value => value.GetString()));

    var summary = analysisJson.GetProperty("sourceSummaries")[0];
    Assert.Equal("source-1", summary.GetProperty("source").GetString());
    Assert.Equal("aac", summary.GetProperty("audioStreams")[0].GetProperty("codecName").GetString());
    Assert.Equal("LC", summary.GetProperty("audioStreams")[0].GetProperty("codecProfile").GetString());
    Assert.Equal(48000, summary.GetProperty("audioStreams")[0].GetProperty("sampleRate").GetInt32());
    Assert.Equal(2, summary.GetProperty("audioStreams")[0].GetProperty("channels").GetInt32());
    Assert.Equal(4.5m, summary.GetProperty("audioStreams")[0].GetProperty("duration").GetDecimal());
    Assert.Equal("mov,mp4,m4a", summary.GetProperty("formatNames").GetString());
    Assert.Equal(1234, summary.GetProperty("sourceByteSize").GetInt64());

    var evidence = order.GetRawText() + analysisJson.GetProperty("sourceSummaries").GetRawText();
    Assert.DoesNotContain("C:\\", evidence, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Secret Chapter", evidence, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Private Ending", evidence, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(firstPath, json.RootElement.GetProperty("orderedSources")[0].GetString());
    Assert.Equal(secondPath, json.RootElement.GetProperty("orderedSources")[1].GetString());
  }

  [Fact]
  public void SanitizeRemovesExtensionlessPathWithoutRemovingTrailingProse()
  {
    var value = PublicTextRedactor.Sanitize("Cannot read C:\\Private Books due to access; retry later", ["C:\\Private Books"]);

    Assert.Equal("Cannot read [source] due to access; retry later", value);
  }

  [Fact]
  public void SanitizeTreatsConnectorWordsInsideUnknownPathsAsPrivate()
  {
    var value = PublicTextRedactor.Sanitize("Cannot read C:\\Private because reasons");

    Assert.Equal("Cannot read [path]", value);
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

  private static SourceFile Source(string fullPath, string relativePath, string codecName, string codecProfile, int sampleRate, int channels, decimal duration, string formatNames, long sourceByteSize)
    => new(fullPath, relativePath, new MediaProbeResult([new AudioTrack(0, codecName, "audio", channels, sampleRate, duration, new Rational(1, sampleRate), new Dictionary<string, string>(), codecProfile)], [], [], new TagCollection([]), new Dictionary<string, string>(), [], duration, formatNames, sourceByteSize));

  private static BookMetadata EmptyMetadata() => new(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());

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
