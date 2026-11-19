using System.Text.Json;
using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Ordering;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.Core.Validation;
using BookSplice.FFmpeg.Commands;
using BookSplice.FFmpeg.Covers;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Tools;
using BookSplice.FFmpeg.Validation;

namespace BookSplice.FFmpeg.Tests.Execution;

public sealed class LongChapterIntegrationTests
{
  private const string InvalidDurationLine = @"Application provided duration: -?[0-9]+ in stream [0-9]+ is invalid";
  private const string Mp3Name = "long.mp3";

  [PinnedMediaFact]
  public async Task EmbeddedCoverAndChapterOverTheOldLimitKeepsTheChapterTrackAtTheAudioRate()
  {
    var directory = Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR");
    if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "ffmpeg.exe"))) throw Xunit.Sdk.SkipException.ForSkip("Pinned FFmpeg tools are not available; set BOOKSPLICE_FFMPEG_DIR to run real-media integration tests.");
    var tools = new MediaToolLocator(directory).Resolve();
    var root = Directory.CreateTempSubdirectory("booksplice-long-chapter-").FullName;
    try
    {
      var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
      var output = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;
      await GenerateMp3WithPictureAsync(tools, root, Path.Combine(source, Mp3Name), 520);
      var runner = new ProcessRunner();
      var probe = new FFprobeMediaProbe(runner, tools);
      var analyzer = new BookAnalyzer(new SourceDiscoverer(probe, 2), new OrderResolver(), new MetadataAggregator(), new CoverDiscoverer(new MediaCoverPayloadOpener(tools)), new ChapterPlanner());
      var service = new ConversionService(analyzer, new ConversionPlanner(new OutputNamePlanner(), new FixedSpace()),
        new FFmpegConversionExecutor(runner, new FFmpegCommandFactory(tools), new Mp4MetadataWriter(), Path.Combine(root, "jobs")),
        new FFmpegOutputValidator(probe, runner, tools, new CoverPayloadValidator()), new AtomicPublisher(),
        new TemporaryArtifactCleaner(Path.Combine(root, "jobs")), new JsonJobLogWriter(Path.Combine(root, "logs")));
      var reviewed = new ReviewedTrackOrder([Mp3Name], []);
      var analysis = await analyzer.AnalyzeAsync(source, true, new BookAnalysisOptions(ReviewedOrder: reviewed));
      Assert.Equal(BookAnalysisStatus.Ready, analysis.Status);
      Assert.Equal(CoverOrigin.EmbeddedPicture, analysis.Cover.Selected?.Origin);
      IReadOnlyList<ChapterMark> marks = [new(0, "Long"), new(500_000_000, "Tail")];
      var settings = AppSettings.Defaults with { OutputDirectory = output, ValidationLevel = ValidationLevel.Full, ConversionJobs = 2 };
      var request = new ConversionRequest(source, true, new ConversionOptions(settings, QualityProfileCatalog.Version1[2], chapterMarks: marks), false,
        new BookAnalysisOptions(ReviewedOrder: reviewed), analysis.OrderedFiles.Select(file => file.FullPath).ToArray(), null,
        analysis.OrderedFiles.Select(file => file.Identity!).ToArray());

      var result = await service.ConvertAsync(request, CancellationToken.None);

      Assert.Equal(ConversionTerminalStatus.Succeeded, result.Status);
      var streams = await ProbeStreamsAsync(tools, result.PublishedPath!);
      var chapterTrack = Assert.Single(streams, stream => stream.Type == "data" || stream.Type == "subtitle");
      Assert.Equal("1/44100", chapterTrack.TimeBase);
      Assert.Equal("1/44100", Assert.Single(streams, stream => stream.Type == "audio").TimeBase);
      Assert.Contains(result.Validation!.Checks, check => check.Code == ValidationCodes.CoverPayload && check.Passed);
    }
    finally { Directory.Delete(root, true); }
  }

  [PinnedMediaFact]
  public async Task ChapterAtTheSplitLimitKeepsTheChapterTrackAndTwoSecondsOverDoesNot()
  {
    var directory = Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR");
    if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "ffmpeg.exe"))) throw Xunit.Sdk.SkipException.ForSkip("Pinned FFmpeg tools are not available; set BOOKSPLICE_FFMPEG_DIR to run real-media integration tests.");
    var tools = new MediaToolLocator(directory).Resolve();
    var root = Directory.CreateTempSubdirectory("booksplice-chapter-limit-").FullName;
    try
    {
      var source = Path.Combine(root, "short.mp3");
      await GenerateMp3WithPictureAsync(tools, root, source, 2);
      var cover = Path.Combine(root, "picture.jpg");
      var limit = LongChapterSplitter.MaximumMicroseconds(44_100);
      var runner = new ProcessRunner();
      var factory = new FFmpegCommandFactory(tools);
      var metadata = new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
      var external = new CoverCandidate("hash", CoverOrigin.ExternalFile, cover, null, "image/jpeg", 32, 32, CoverSemanticType.FrontCover, true);
      var results = new List<(long End, StreamInfo[] Streams, string StandardError)>();
      foreach (var end in new[] { limit, limit + 2_000_000 })
      {
        var chapters = new[] { new ChapterEntry(0, end, "Long", "", "short.mp3") };
        var plan = new ConversionPlan([source], metadata, external, chapters, QualityProfileCatalog.Version1[2], ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, Path.Combine(root, "planned.m4b"), AudioStrategy.DirectTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 1, "integration", MetadataProfiles.GenericMp4, 44_100, 1);
        var ffmetadata = Path.Combine(root, $"chapters-{end}.ffmeta");
        await File.WriteAllTextAsync(ffmetadata, $";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000000\nSTART=0\nEND={end}\ntitle=Long\n");
        var output = Path.Combine(root, $"out-{end}.m4b");
        var result = await runner.RunAsync(factory.CreateFinal(plan, output, metadataFile: ffmetadata), null, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        results.Add((end, await ProbeStreamsAsync(tools, output), result.StandardError));
      }

      var kept = Assert.Single(results[0].Streams, stream => stream.Type == "data" || stream.Type == "subtitle");
      Assert.Equal("1/44100", kept.TimeBase);
      Assert.DoesNotMatch(InvalidDurationLine, results[0].StandardError);
      Assert.Matches(InvalidDurationLine, results[1].StandardError);
      Assert.DoesNotContain(results[1].Streams, stream => (stream.Type == "data" || stream.Type == "subtitle") && stream.TimeBase == "1/44100");
    }
    finally { Directory.Delete(root, true); }
  }

  private static async Task GenerateMp3WithPictureAsync(MediaToolSet tools, string root, string path, int seconds)
  {
    var runner = new ProcessRunner();
    var image = Path.Combine(root, "picture.jpg");
    var made = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=32x32:d=1", "-frames:v", "1", "-c:v", "mjpeg", image]), null, CancellationToken.None);
    Assert.Equal(0, made.ExitCode);
    var generatorDirectory = Environment.GetEnvironmentVariable("BOOKSPLICE_FIXTURE_FFMPEG_DIR");
    var generator = string.IsNullOrWhiteSpace(generatorDirectory) ? tools.FFmpegPath : new MediaToolLocator(generatorDirectory).Resolve().FFmpegPath;
    var args = new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"anullsrc=r=44100:cl=mono:d={seconds}", "-i", image, "-map", "0:a:0", "-map", "1:v:0", "-c:a", "libmp3lame", "-b:a", "32k", "-c:v", "copy", "-id3v2_version", "3", path };
    var result = await runner.RunAsync(new ProcessSpec(generator, args), null, CancellationToken.None);
    Assert.Equal(0, result.ExitCode);
  }

  private static async Task<StreamInfo[]> ProbeStreamsAsync(MediaToolSet tools, string path)
  {
    var result = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFprobePath, ["-v", "error", "-show_entries", "stream=index,codec_type,time_base", "-of", "json", path]), null, CancellationToken.None);
    Assert.Equal(0, result.ExitCode);
    using var document = JsonDocument.Parse(result.StandardOutput);
    return document.RootElement.GetProperty("streams").EnumerateArray()
      .Select(stream => new StreamInfo(stream.GetProperty("codec_type").GetString()!, stream.GetProperty("time_base").GetString()!)).ToArray();
  }

  private readonly record struct StreamInfo(string Type, string TimeBase);

  private sealed class FixedSpace : IStorageSpaceProvider { public long? GetAvailableBytes(string destinationDirectory) => long.MaxValue; }
}
