using System.IO;
using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.EditCopy;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Ordering;
using BookSplice.Core.Planning;
using BookSplice.Core.Preview;
using BookSplice.Core.Settings;
using BookSplice.Core.Validation;
using BookSplice.FFmpeg.Commands;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Preview;
using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Tools;
using BookSplice.FFmpeg.Validation;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Bootstrap;

public sealed class AppHost(string localAppDataRoot)
{
  public ISettingsStore Settings { get; } = new JsonSettingsStore(localAppDataRoot);
  public MetadataProfileCatalog Profiles { get; } = MetadataProfileCatalog.Load(Path.Combine(Path.GetFullPath(localAppDataRoot), "BookSplice", "profiles"));

  public GuiServices CreateServices(string mediaToolDirectory)
  {
    var tools = new MediaToolLocator(mediaToolDirectory).Resolve();
    var runner = new ProcessRunner();
    var probe = new FFprobeMediaProbe(runner, tools);
    var analyzer = new BookAnalyzer(new SourceDiscoverer(probe, Math.Clamp(Environment.ProcessorCount, 1, 8)),
      new OrderResolver(), new MetadataAggregator(), new CoverDiscoverer(new BookSplice.FFmpeg.Covers.MediaCoverPayloadOpener(tools)), new ChapterPlanner());
    var localDirectory = Path.Combine(Path.GetFullPath(localAppDataRoot), "BookSplice");
    var temporaryRoot = Path.Combine(localDirectory, "temp");
    var jobLog = new JsonJobLogWriter(Path.Combine(localDirectory, "logs"));
    var planner = new ConversionPlanner(new OutputNamePlanner(), new StorageSpaceProvider(), Profiles, temporaryRoot);
    var service = new ConversionService(analyzer, planner,
      new FFmpegConversionExecutor(runner, new FFmpegCommandFactory(tools), new Mp4MetadataWriter(), temporaryRoot),
      new FFmpegOutputValidator(probe, runner, tools, new CoverPayloadValidator()),
      new AtomicPublisher(), new TemporaryArtifactCleaner(temporaryRoot),
      jobLog);
    var previewRoot = Path.Combine(temporaryRoot, "preview");
    PreviewViewModel.Sweep(previewRoot);
    return new(analyzer, planner, service, new BookSplice.FFmpeg.Covers.MediaCoverPayloadOpener(tools), Profiles, new ExistingM4bChecker(probe, runner, tools), jobLog, new PreviewDecoder(runner, tools), previewRoot, EditCopy: new BookSplice.FFmpeg.EditCopy.EditCopyService(runner, tools, temporaryRoot));
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

public sealed record GuiServices(IBookAnalyzer Analyzer, IConversionPlanner Planner, IConversionService Conversion, ICoverPayloadOpener CoverPayloads, MetadataProfileCatalog? Profiles = null, IExistingFileChecker? Checker = null, IJobLogWriter? JobLog = null, IPreviewDecoder? PreviewDecoder = null, string? PreviewRoot = null, Func<IAudioPlayer>? PreviewPlayerFactory = null, IEditCopyService? EditCopy = null);
