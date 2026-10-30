using System.Security.Cryptography;
using BookSplice.Core.Analysis;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Chapters;
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

public sealed class CustomTrackOrderIntegrationTests
{
  [PinnedMediaFact]
  public async Task CustomOrderAndExclusionAreAppliedToAnalysisPlanAndOutputWithoutTouchingSources()
  {
    using var fixture = await Fixture.CreateAsync();
    var (intro, one, two) = ("00 intro.m4a", "01 one.m4a", "02 two.m4a");
    await fixture.GenerateAsync(intro, 440, 1, withCover: true);
    await fixture.GenerateAsync(one, 550, 2);
    await fixture.GenerateAsync(two, 660, 3);
    var before = fixture.Hashes();
    var reviewed = new ReviewedTrackOrder([two, one], [intro]);

    var analysis = await fixture.Analyzer.AnalyzeAsync(fixture.Source, true, new BookAnalysisOptions(ReviewedOrder: reviewed));

    Assert.Equal(BookAnalysisStatus.Ready, analysis.Status);
    Assert.Equal([two, one], analysis.OrderedFiles.Select(file => file.RelativePath));
    Assert.Equal([intro], analysis.ExcludedFiles.Select(file => file.RelativePath));
    Assert.Equal([two, one], analysis.Chapters.Entries.Select(chapter => chapter.SourceRelativePath));
    Assert.InRange(analysis.Chapters.TotalDurationMicroseconds, 4_800_000, 5_400_000);
    Assert.Empty(analysis.Cover.Candidates);

    var request = fixture.Request(analysis, reviewed);
    var result = await fixture.Service.ConvertAsync(request, CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.Succeeded, result.Status);
    Assert.Equal(analysis.OrderedFiles.Select(file => file.FullPath), result.Planning!.Plan!.SourcePaths);
    Assert.Equal(2, result.Planning.Plan.Chapters.Count);
    var facts = await fixture.Probe.ProbeAsync(result.PublishedPath!);
    Assert.InRange(facts.Duration!.Value, 4.8m, 5.4m);
    Assert.Empty(facts.AttachedPictures);
    Assert.Equal(2, facts.Chapters.Count);
    Assert.Equal(before, fixture.Hashes());
    foreach (var log in Directory.EnumerateFiles(fixture.Logs, "*", SearchOption.AllDirectories))
      Assert.DoesNotContain(fixture.Source, await File.ReadAllTextAsync(log), StringComparison.OrdinalIgnoreCase);
  }

  [PinnedMediaFact]
  public async Task EmbeddedCoverFromAnIncludedTrackStillSurvivesExclusionOfAnother()
  {
    using var fixture = await Fixture.CreateAsync();
    await fixture.GenerateAsync("a.m4a", 440, 1);
    await fixture.GenerateAsync("b.m4a", 550, 1, withCover: true);

    var analysis = await fixture.Analyzer.AnalyzeAsync(fixture.Source, true, new BookAnalysisOptions(ReviewedOrder: new(["b.m4a"], ["a.m4a"])));

    Assert.Equal(BookAnalysisStatus.Ready, analysis.Status);
    Assert.Single(analysis.Cover.Candidates);
  }

  [PinnedMediaFact]
  public async Task ReviewedOrderIsRefusedWhenItIsEmptyDuplicatedOrNamesAnUnknownFile()
  {
    using var fixture = await Fixture.CreateAsync();
    await fixture.GenerateAsync("a.m4a", 440, 1);
    await fixture.GenerateAsync("b.m4a", 550, 1);

    var empty = await fixture.Analyzer.AnalyzeAsync(fixture.Source, true, new BookAnalysisOptions(ReviewedOrder: new([], ["a.m4a", "b.m4a"])));
    var duplicate = await fixture.Analyzer.AnalyzeAsync(fixture.Source, true, new BookAnalysisOptions(ReviewedOrder: new(["a.m4a", "a.m4a"], ["b.m4a"])));
    var unknown = await fixture.Analyzer.AnalyzeAsync(fixture.Source, true, new BookAnalysisOptions(ReviewedOrder: new(["a.m4a", "gone.m4a"], ["b.m4a"])));

    Assert.Equal(BookAnalysisStatus.Invalid, empty.Status);
    Assert.Contains(empty.Diagnostics, d => d.Code == "analysis.order-empty");
    Assert.Equal(BookAnalysisStatus.Invalid, duplicate.Status);
    Assert.Equal(BookAnalysisStatus.NeedsDecision, unknown.Status);
    Assert.All([empty, duplicate, unknown], analysis => Assert.Empty(analysis.OrderedFiles));
  }

  [PinnedMediaFact]
  public async Task ANewFileAfterReviewRequiresANewDecision()
  {
    using var fixture = await Fixture.CreateAsync();
    await fixture.GenerateAsync("a.m4a", 440, 1);
    await fixture.GenerateAsync("b.m4a", 550, 1);
    var reviewed = new ReviewedTrackOrder(["b.m4a"], ["a.m4a"]);
    var analysis = await fixture.Analyzer.AnalyzeAsync(fixture.Source, true, new BookAnalysisOptions(ReviewedOrder: reviewed));
    var request = fixture.Request(analysis, reviewed, dryRun: true);
    await fixture.GenerateAsync("c.m4a", 660, 1);

    var result = await fixture.Service.ConvertAsync(request, CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.DecisionRequired, result.Status);
    Assert.Contains(result.Diagnostics, d => d.Code == "analysis.order-sources-changed");
    Assert.Null(result.Planning);
  }

  [PinnedMediaFact]
  public async Task AReplacedIncludedFileInvalidatesTheReviewButAChangedExcludedFileDoesNot()
  {
    using var fixture = await Fixture.CreateAsync();
    await fixture.GenerateAsync("a.m4a", 440, 1);
    await fixture.GenerateAsync("b.m4a", 550, 1);
    await fixture.GenerateAsync("c.m4a", 660, 1);
    var reviewed = new ReviewedTrackOrder(["c.m4a", "b.m4a"], ["a.m4a"]);
    var analysis = await fixture.Analyzer.AnalyzeAsync(fixture.Source, true, new BookAnalysisOptions(ReviewedOrder: reviewed));
    var request = fixture.Request(analysis, reviewed, dryRun: true);

    await fixture.GenerateAsync("a.m4a", 880, 2);
    var excludedChanged = await fixture.Service.ConvertAsync(request, CancellationToken.None);
    await fixture.GenerateAsync("b.m4a", 880, 2);
    var includedChanged = await fixture.Service.ConvertAsync(request, CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.DryRun, excludedChanged.Status);
    Assert.Equal(ConversionTerminalStatus.DecisionRequired, includedChanged.Status);
    Assert.Contains(includedChanged.Diagnostics, d => d.Code == "analysis.sources-changed");
  }

  [PinnedMediaFact]
  public async Task AGroupedSelectionAcceptsACustomOrderAcrossFolders()
  {
    using var fixture = await Fixture.CreateAsync();
    await fixture.GenerateAsync("Disc1/a.m4a", 440, 1);
    await fixture.GenerateAsync("Disc2/b.m4a", 550, 1);
    await fixture.GenerateAsync("Disc2/c.m4a", 660, 1);
    var selection = SourceSelection.Create(new List<string> { "Disc1/a.m4a", "Disc2/b.m4a", "Disc2/c.m4a" }.Select(path => Path.Combine(fixture.Source, path)));
    var reviewed = new ReviewedTrackOrder(["Disc2/c.m4a", "Disc1/a.m4a"], ["Disc2/b.m4a"]);

    var analysis = await fixture.Analyzer.AnalyzeAsync(selection, true, new BookAnalysisOptions(ReviewedOrder: reviewed));
    var result = await fixture.Service.ConvertAsync(fixture.Request(analysis, reviewed, dryRun: true, selection: selection), CancellationToken.None);

    Assert.Equal(BookAnalysisStatus.Ready, analysis.Status);
    Assert.Equal(["Disc2/c.m4a", "Disc1/a.m4a"], analysis.OrderedFiles.Select(file => file.RelativePath));
    Assert.Equal(ConversionTerminalStatus.DryRun, result.Status);
    Assert.Equal(analysis.OrderedFiles.Select(file => file.FullPath), result.Planning!.Plan!.SourcePaths);
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
      var root = Directory.CreateTempSubdirectory("booksplice-trackorder-").FullName;
      return Task.FromResult(new Fixture(root, new MediaToolLocator(directory).Resolve()));
    }

    public async Task GenerateAsync(string name, int frequency, int seconds, bool withCover = false)
    {
      var path = Path.Combine(Source, name);
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      var runner = new ProcessRunner();
      var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency={frequency}:duration={seconds}" };
      var picture = Path.Combine(_root, "art.jpg");
      if (withCover)
      {
        var image = await runner.RunAsync(new ProcessSpec(_tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=32x32:d=1", "-frames:v", "1", picture]), null, CancellationToken.None);
        Assert.Equal(0, image.ExitCode);
        args.AddRange(["-i", picture, "-map", "0:a:0", "-map", "1:v:0", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
      }
      args.AddRange(["-ac", "1", "-ar", "44100", "-c:a", "aac", "-b:a", "96k", path]);
      var result = await runner.RunAsync(new ProcessSpec(_tools.FFmpegPath, args), null, CancellationToken.None);
      Assert.Equal(0, result.ExitCode);
    }

    public Dictionary<string, string> Hashes() => Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories)
      .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    public ConversionRequest Request(BookAnalysis analysis, ReviewedTrackOrder reviewed, bool dryRun = false, SourceSelection? selection = null)
    {
      var settings = AppSettings.Defaults with { OutputDirectory = Output, ValidationLevel = ValidationLevel.Lightweight, ConversionJobs = 2 };
      return new ConversionRequest(selection?.RootPath ?? Source, true, new ConversionOptions(settings, QualityProfileCatalog.Version1[2]), dryRun,
        new BookAnalysisOptions(ReviewedOrder: reviewed), analysis.OrderedFiles.Select(file => file.FullPath).ToArray(), selection,
        analysis.OrderedFiles.Select(file => file.Identity!).ToArray());
    }

    public void Dispose()
    {
      if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
  }

  private sealed class FixedSpace : IStorageSpaceProvider { public long? GetAvailableBytes(string destinationDirectory) => long.MaxValue; }
}
