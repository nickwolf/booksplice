using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Ordering;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.FFmpeg.Commands;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Tools;
using BookSplice.FFmpeg.Validation;

namespace BookSplice.Cli;

public static class CliComposition
{
  public static CliApplication Create(string localAppDataRoot, string mediaToolDirectory)
  {
    var localDirectory = Path.Combine(Path.GetFullPath(localAppDataRoot), "BookSplice");
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
      new CoverDiscoverer(new BookSplice.FFmpeg.Covers.MediaCoverPayloadOpener(tools)),
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
