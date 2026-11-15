using System.Security.Cryptography;
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

public sealed class ChapterMarksIntegrationTests
{
  [PinnedMediaFact]
  public async Task EditedChapterMarksArePublishedWithExactStartsAndTitles()
  {
    using var fixture = await Fixture.CreateAsync();
    var (one, two, three) = ("01 one.m4a", "02 two.m4a", "03 three.m4a");
    await fixture.GenerateAsync(one, 440, 20);
    await fixture.GenerateAsync(two, 550, 20);
    await fixture.GenerateAsync(three, 660, 20);
    var before = fixture.Hashes();
    var reviewed = new ReviewedTrackOrder([one, two, three], []);
    var analysis = await fixture.Analyzer.AnalyzeAsync(fixture.Source, true, new BookAnalysisOptions(ReviewedOrder: reviewed));
    Assert.Equal(BookAnalysisStatus.Ready, analysis.Status);
    var total = analysis.Chapters.TotalDurationMicroseconds;
    var track = ChapterMarks.FromChapterPlan(analysis.Chapters);
    Assert.Equal(3, track.Count);

    var split = ChapterMarks.Split(track, 0, 7_500_000, total);
    Assert.True(split.IsAccepted, string.Join(", ", split.Problems.Select(problem => problem.Message)));
    var merged = ChapterMarks.MergeWithNext(split.Marks!, 2, total);
    Assert.True(merged.IsAccepted, string.Join(", ", merged.Problems.Select(problem => problem.Message)));
    var marks = merged.Marks!;
    Assert.Equal([0L, 7_500_000L, 20_000_000L], marks.Select(mark => mark.StartMicroseconds));

    var result = await fixture.Service.ConvertAsync(fixture.Request(analysis, reviewed, marks), CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.Succeeded, result.Status);
    var facts = await fixture.Probe.ProbeAsync(result.PublishedPath!);
    Assert.Equal(3, facts.Chapters.Count);
    var expectedStarts = new[] { 0m, 7.5m, 20m };
    for (var index = 0; index < 3; index++)
    {
      Assert.True(Math.Abs(facts.Chapters[index].StartTime - expectedStarts[index]) <= 0.001m, $"Chapter {index} starts at {facts.Chapters[index].StartTime}.");
      Assert.Equal(marks[index].Title, facts.Chapters[index].RawTags["title"]);
    }
    var planned = analysis.Chapters.TotalDurationMicroseconds / 1_000_000m;
    var audioEnd = facts.AudioStreams.Single().Duration!.Value;
    // Stream-copied AAC keeps each source's priming, so the audio can run slightly past the planned total; that gap is not about chapter marks.
    Assert.True(Math.Abs(facts.Chapters[^1].EndTime - planned) <= 0.001m, $"Last chapter ends at {facts.Chapters[^1].EndTime}; planned total is {planned}.");
    Assert.True(facts.Chapters[^1].EndTime <= audioEnd + 0.001m, $"Last chapter ends at {facts.Chapters[^1].EndTime}, past the audio at {audioEnd}.");
    Assert.Equal(before, fixture.Hashes());
  }

  [PinnedMediaFact]
  public async Task ChapterMarksThatFailValidationAreRefusedAndNothingIsPublished()
  {
    using var fixture = await Fixture.CreateAsync();
    var (one, two) = ("01 one.m4a", "02 two.m4a");
    await fixture.GenerateAsync(one, 440, 20);
    await fixture.GenerateAsync(two, 550, 20);
    var reviewed = new ReviewedTrackOrder([one, two], []);
    var analysis = await fixture.Analyzer.AnalyzeAsync(fixture.Source, true, new BookAnalysisOptions(ReviewedOrder: reviewed));
    Assert.Equal(BookAnalysisStatus.Ready, analysis.Status);
    IReadOnlyList<ChapterMark> marks = [new(0, "Opening"), new(500_000, "Too short"), new(20_000_000, "Rest")];

    var result = await fixture.Service.ConvertAsync(fixture.Request(analysis, reviewed, marks), CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.InvalidInput, result.Status);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "planning.chapter-marks-invalid");
    Assert.Null(result.PublishedPath);
    Assert.Empty(Directory.EnumerateFiles(fixture.Output));
  }

  private sealed class Fixture : IDisposable
  {
    private readonly string _root;
    private readonly MediaToolSet _tools;
    private Fixture(string root, MediaToolSet tools)
    {
      _root = root;
      _tools = tools;
      Source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
      Output = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;
      Logs = Path.Combine(root, "logs");
      Probe = new FFprobeMediaProbe(new ProcessRunner(), tools);
      Analyzer = new BookAnalyzer(new SourceDiscoverer(Probe, 2), new OrderResolver(), new MetadataAggregator(), new CoverDiscoverer(new MediaCoverPayloadOpener(tools)), new ChapterPlanner());
      var runner = new ProcessRunner();
      Service = new ConversionService(Analyzer, new ConversionPlanner(new OutputNamePlanner(), new FixedSpace()),
        new FFmpegConversionExecutor(runner, new FFmpegCommandFactory(tools), new Mp4MetadataWriter(), Path.Combine(root, "jobs")),
        new FFmpegOutputValidator(Probe, runner, tools, new CoverPayloadValidator()), new AtomicPublisher(),
        new TemporaryArtifactCleaner(Path.Combine(root, "jobs")), new JsonJobLogWriter(Logs));
    }

    public string Source { get; }
    public string Output { get; }
    public string Logs { get; }
    public FFprobeMediaProbe Probe { get; }
    public BookAnalyzer Analyzer { get; }
    public ConversionService Service { get; }

    public static Task<Fixture> CreateAsync()
    {
      var directory = Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR");
      if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "ffmpeg.exe"))) throw Xunit.Sdk.SkipException.ForSkip("Pinned FFmpeg tools are not available; set BOOKSPLICE_FFMPEG_DIR to run real-media integration tests.");
      var root = Directory.CreateTempSubdirectory("booksplice-chapter-marks-").FullName;
      return Task.FromResult(new Fixture(root, new MediaToolLocator(directory).Resolve()));
    }

    public async Task GenerateAsync(string name, int frequency, int seconds)
    {
      var path = Path.Combine(Source, name);
      var runner = new ProcessRunner();
      var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency={frequency}:duration={seconds}" };
      args.AddRange(["-ac", "1", "-ar", "44100", "-c:a", "aac", "-b:a", "96k", path]);
      var result = await runner.RunAsync(new ProcessSpec(_tools.FFmpegPath, args), null, CancellationToken.None);
      Assert.Equal(0, result.ExitCode);
    }

    public Dictionary<string, string> Hashes() => Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories)
      .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    public ConversionRequest Request(BookAnalysis analysis, ReviewedTrackOrder reviewed, IReadOnlyList<ChapterMark> marks)
    {
      var settings = AppSettings.Defaults with { OutputDirectory = Output, ValidationLevel = ValidationLevel.Full, ConversionJobs = 2 };
      var options = new ConversionOptions(settings, QualityProfileCatalog.Version1[2], chapterMarks: marks);
      return new ConversionRequest(Source, true, options, false,
        new BookAnalysisOptions(ReviewedOrder: reviewed), analysis.OrderedFiles.Select(file => file.FullPath).ToArray(), null,
        analysis.OrderedFiles.Select(file => file.Identity!).ToArray());
    }

    public void Dispose()
    {
      if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
  }

  private sealed class FixedSpace : IStorageSpaceProvider { public long? GetAvailableBytes(string destinationDirectory) => long.MaxValue; }
}
