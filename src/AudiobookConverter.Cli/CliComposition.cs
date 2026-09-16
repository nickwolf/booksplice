using AudiobookConverter.Core.Analysis;
using AudiobookConverter.Core.Chapters;
using AudiobookConverter.Core.Covers;
using AudiobookConverter.Core.Discovery;
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
    var tools = new MediaToolLocator(mediaToolDirectory).Resolve();
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
      new JsonJobLogWriter(logDirectory));
    return new CliApplication(new JsonSettingsStore(localAppDataRoot), service);
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
