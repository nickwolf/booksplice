using AudiobookConverter.Core.Analysis;
using AudiobookConverter.Core.Chapters;
using AudiobookConverter.Core.Covers;
using AudiobookConverter.Core.Discovery;
using AudiobookConverter.Core.Execution;
using AudiobookConverter.Core.Metadata;
using AudiobookConverter.Core.Naming;
using AudiobookConverter.Core.Ordering;
using AudiobookConverter.Core.Planning;
using AudiobookConverter.Core.Settings;
using AudiobookConverter.FFmpeg.Commands;
using AudiobookConverter.FFmpeg.Execution;
using AudiobookConverter.FFmpeg.Probing;
using AudiobookConverter.FFmpeg.Tools;
using AudiobookConverter.FFmpeg.Validation;

namespace AudiobookConverter.Cli;

public static class CliComposition
{
  public static CliApplication Create(string localAppDataRoot, string mediaToolDirectory)
  {
    var localDirectory = Path.Combine(Path.GetFullPath(localAppDataRoot), "AudiobookConverter");
    var temporaryRoot = Path.Combine(localDirectory, "temp");
    var logDirectory = Path.Combine(localDirectory, "logs");
    var logs = new JsonJobLogWriter(logDirectory);
    MediaToolSet tools;
    try
    {
      tools = new MediaToolLocator(mediaToolDirectory).Resolve();
    }
    catch (MediaToolException)
    {
      return new CliApplication(
        new JsonSettingsStore(localAppDataRoot),
        new UnavailableToolConversionService(logs),
        logs);
    }
    var runner = new ProcessRunner();
    var probe = new FFprobeMediaProbe(runner, tools);
    var analyzer = new BookAnalyzer(
      new SourceDiscoverer(probe, Math.Clamp(Environment.ProcessorCount, 1, 8)),
      new OrderResolver(),
      new MetadataAggregator(),
      new CoverDiscoverer(),
      new ChapterPlanner());
    var planner = new ConversionPlanner(new OutputNamePlanner(), new FileSystemStorageSpaceProvider());
    var executor = new FFmpegConversionExecutor(runner, new FFmpegCommandFactory(tools), new Mp4MetadataWriter(), temporaryRoot);
    var validator = new FFmpegOutputValidator(probe, runner, tools, new CoverPayloadValidator());
    var service = new ConversionService(
      analyzer,
      planner,
      executor,
      validator,
      new AtomicPublisher(),
      new TemporaryArtifactCleaner(temporaryRoot),
      logs);
    return new CliApplication(new JsonSettingsStore(localAppDataRoot), service, logs);
  }

  private sealed class UnavailableToolConversionService(IJobLogWriter logs) : IConversionService
  {
    public async Task<ConversionServiceResult> ConvertAsync(
      ConversionRequest request,
      CancellationToken cancellationToken,
      IProgress<ConversionProgress>? progress = null)
    {
      var jobId = Guid.NewGuid();
      var startedAt = DateTimeOffset.UtcNow;
      var wasCancelled = cancellationToken.IsCancellationRequested;
      IReadOnlyList<ServiceDiagnostic> diagnostics =
      [
        wasCancelled
          ? new("conversion.cancelled", ServiceDiagnosticSeverity.Information, "The conversion was cancelled.")
          : new("tools.unavailable", ServiceDiagnosticSeverity.Error, "The required media tools are unavailable."),
      ];
      var result = new ConversionServiceResult(
        jobId,
        wasCancelled ? ConversionTerminalStatus.Cancelled : ConversionTerminalStatus.ExecutionFailed,
        ConversionStage.Execution,
        null,
        null,
        null,
        null,
        null,
        diagnostics,
        [],
        null);

      if (request.DryRun) return result;

      var record = new ConversionAuditRecord(
        ConversionAuditRecord.CurrentSchemaVersion,
        jobId,
        startedAt,
        DateTimeOffset.UtcNow,
        result.Status,
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
        diagnostics,
        null);
      try
      {
        await logs.WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
      }
      catch (Exception)
      {
        diagnostics =
        [
          .. diagnostics,
          new("audit.write-failed", ServiceDiagnosticSeverity.Warning, "The job audit record could not be written."),
        ];
        result = result with { Diagnostics = diagnostics };
      }

      return result;
    }
  }

  private sealed class FileSystemStorageSpaceProvider : IStorageSpaceProvider
  {
    public long? GetAvailableBytes(string destinationDirectory)
    {
      try
      {
        var root = Path.GetPathRoot(Path.GetFullPath(destinationDirectory));
        return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root).AvailableFreeSpace;
      }
      catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
      {
        return null;
      }
    }
  }
}
