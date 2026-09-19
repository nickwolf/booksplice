using System.IO;
using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
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

namespace BookSplice.Gui.Bootstrap;

public sealed class AppHost(string localAppDataRoot)
{
  public ISettingsStore Settings { get; } = new JsonSettingsStore(localAppDataRoot);

  public GuiServices CreateServices(string mediaToolDirectory)
  {
    var tools = new MediaToolLocator(mediaToolDirectory).Resolve();
    var runner = new ProcessRunner();
    var probe = new FFprobeMediaProbe(runner, tools);
    var analyzer = new BookAnalyzer(new SourceDiscoverer(probe, Math.Clamp(Environment.ProcessorCount, 1, 8)),
      new OrderResolver(), new MetadataAggregator(), new CoverDiscoverer(new BookSplice.FFmpeg.Covers.MediaCoverPayloadOpener(tools)), new ChapterPlanner());
    var planner = new ConversionPlanner(new OutputNamePlanner(), new StorageSpaceProvider());
    var localDirectory = Path.Combine(Path.GetFullPath(localAppDataRoot), "BookSplice");
    var temporaryRoot = Path.Combine(localDirectory, "temp");
    var service = new ConversionService(analyzer, planner,
      new FFmpegConversionExecutor(runner, new FFmpegCommandFactory(tools), new Mp4MetadataWriter(), temporaryRoot),
      new FFmpegOutputValidator(probe, runner, tools, new CoverPayloadValidator()),
      new AtomicPublisher(), new TemporaryArtifactCleaner(temporaryRoot),
      new JsonJobLogWriter(Path.Combine(localDirectory, "logs")));
    return new(analyzer, planner, service, new BookSplice.FFmpeg.Covers.MediaCoverPayloadOpener(tools));
  }

  private sealed class StorageSpaceProvider : IStorageSpaceProvider
  {
    public long? GetAvailableBytes(string destinationDirectory)
    {
      try
      {
        var root = Path.GetPathRoot(Path.GetFullPath(destinationDirectory));
        return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root).AvailableFreeSpace;
      }
      catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }
  }
}

public sealed record GuiServices(IBookAnalyzer Analyzer, IConversionPlanner Planner, IConversionService Conversion, ICoverPayloadOpener CoverPayloads);
