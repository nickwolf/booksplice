using AudiobookConverter.Core.Chapters;
using AudiobookConverter.Core.Covers;
using AudiobookConverter.Core.Metadata;
using AudiobookConverter.Core.Naming;
using AudiobookConverter.Core.Planning;
using AudiobookConverter.Core.Settings;
using AudiobookConverter.FFmpeg.Commands;
using AudiobookConverter.FFmpeg.Execution;
using AudiobookConverter.FFmpeg.Probing;
using AudiobookConverter.FFmpeg.Tools;

namespace AudiobookConverter.FFmpeg.Tests.Execution;

public sealed class FFmpegRealMediaIntegrationTests
{
  [PinnedMediaFact]
  public async Task EveryStrategyProducesOneParseableAudioStreamAndLeavesDestinationUntouched()
  {
    var tools = ResolveTools();
    using var root = new TemporaryDirectory();
    var sourceRoot = Directory.CreateDirectory(Path.Combine(root.Path, "generated-sources")).FullName;
    var mp3Sources = await Task.WhenAll(
      GenerateAudioAsync(tools, Path.Combine(sourceRoot, "one.mp3"), "libmp3lame", "1", "44100", "440"),
      GenerateAudioAsync(tools, Path.Combine(sourceRoot, "two.mp3"), "libmp3lame", "1", "44100", "550"),
      GenerateAudioAsync(tools, Path.Combine(sourceRoot, "three.mp3"), "libmp3lame", "1", "44100", "660"));
    var aacSources = await Task.WhenAll(
      GenerateAudioAsync(tools, Path.Combine(sourceRoot, "copy-one.m4a"), "aac", "1", "44100", "440"),
      GenerateAudioAsync(tools, Path.Combine(sourceRoot, "copy-two.m4a"), "aac", "1", "44100", "550"));

    var cases = new Dictionary<AudioStrategy, IReadOnlyList<string>>
    {
      [AudioStrategy.AacStreamCopy] = aacSources,
      [AudioStrategy.DirectTranscode] = [mp3Sources[0]],
      [AudioStrategy.FilterConcatTranscode] = [mp3Sources[0], mp3Sources[1]],
      [AudioStrategy.SegmentedTranscode] = mp3Sources,
    };

    foreach (var (strategy, sources) in cases)
    {
      var destination = Path.Combine(root.Path, "destinations", strategy + ".m4b");
      Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
      var sentinel = Path.Combine(Path.GetDirectoryName(destination)!, "keep.txt");
      await File.WriteAllTextAsync(sentinel, "destination sentinel");
      var runner = new RecordingRunner(new ProcessRunner());
      var jobRoot = Path.Combine(root.Path, "jobs", strategy.ToString());
      var plan = CreatePlan(strategy, sources, destination, sourceRoot);
      var result = await new FFmpegConversionExecutor(
        runner,
        new FFmpegCommandFactory(tools),
        new Mp4MetadataWriter(),
        jobRoot).ExecuteAsync(plan, CancellationToken.None);

      Assert.Equal(ExecutionStatus.Succeeded, result.Status);
      Assert.NotNull(result.TemporaryOutputPath);
      Assert.True(File.Exists(result.TemporaryOutputPath));
      Assert.False(File.Exists(destination));
      Assert.Equal("destination sentinel", await File.ReadAllTextAsync(sentinel));

      var media = await new FFprobeMediaProbe(new ProcessRunner(), tools).ProbeAsync(result.TemporaryOutputPath!);
      Assert.Single(media.AudioStreams);
      Assert.Equal("aac", media.AudioStreams[0].CodecName);
      Assert.Contains("mp4", media.FormatNames, StringComparison.OrdinalIgnoreCase);

      var ffmpegSpecs = runner.Specs.Where(spec => spec.FileName.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
      if (strategy == AudioStrategy.SegmentedTranscode)
      {
        Assert.Equal(sources.Count + 1, ffmpegSpecs.Length);
        Assert.Equal(sources.Count, ffmpegSpecs.Count(spec => ValueAfter(spec, "-c:a") == "aac"));
        Assert.Equal(1, ffmpegSpecs.Count(spec => ValueAfter(spec, "-c:a") == "copy"));
      }
      else
      {
        Assert.Single(ffmpegSpecs);
        var codec = ValueAfter(ffmpegSpecs[0], "-c:a");
        Assert.Equal(strategy == AudioStrategy.AacStreamCopy ? "copy" : "aac", codec);
        Assert.Equal(1, ffmpegSpecs[0].Arguments.Count(argument => argument == "-c:a"));
      }
    }
  }

  [PinnedMediaFact]
  public async Task StreamCopyUsesRealConcatManifestWithApostropheAndUnicodePath()
  {
    var tools = ResolveTools();
    using var root = new TemporaryDirectory();
    var sourceRoot = Directory.CreateDirectory(Path.Combine(root.Path, "sources")).FullName;
    var first = await GenerateAudioAsync(tools, Path.Combine(sourceRoot, "normal.m4a"), "aac", "1", "44100", "440");
    var second = await GenerateAudioAsync(tools, Path.Combine(sourceRoot, "L'auteur-日本語.m4a"), "aac", "1", "44100", "550");
    var jobRoot = Path.Combine(root.Path, "job manifest's-日本語");
    var destination = Path.Combine(root.Path, "destination.m4b");
    var runner = new RecordingRunner(new ProcessRunner());

    var result = await new FFmpegConversionExecutor(
      runner,
      new FFmpegCommandFactory(tools),
      new Mp4MetadataWriter(),
      jobRoot).ExecuteAsync(CreatePlan(AudioStrategy.AacStreamCopy, [first, second], destination, sourceRoot), CancellationToken.None);

    Assert.Equal(ExecutionStatus.Succeeded, result.Status);
    Assert.NotNull(result.TemporaryOutputPath);
    var final = runner.Specs.Last(spec => spec.FileName.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
    var manifest = final.Arguments[final.Arguments.ToList().IndexOf("-i") + 1];
    Assert.Contains("manifest's-日本語", manifest);
    Assert.True(File.Exists(manifest));
    var contents = await File.ReadAllTextAsync(manifest);
    Assert.Contains("L\\'auteur-日本語.m4a", contents);
    var media = await new FFprobeMediaProbe(new ProcessRunner(), tools).ProbeAsync(result.TemporaryOutputPath!);
    Assert.Single(media.AudioStreams);
    Assert.Equal("aac", media.AudioStreams[0].CodecName);
  }

  [PinnedMediaFact]
  public async Task FinalMuxRetainsMetadataChapterAndExternalJpegAndPngCoverStructure()
  {
    var tools = ResolveTools();
    using var root = new TemporaryDirectory();
    var source = await GenerateAudioAsync(tools, Path.Combine(root.Path, "spoken.mp3"), "libmp3lame", "1", "44100", "440");
    foreach (var cover in new[] { ("jpg", "mjpeg", "red"), ("png", "png", "blue") })
    {
      var coverPath = Path.Combine(root.Path, $"cover.{cover.Item1}");
      await GenerateImageAsync(tools, coverPath, cover.Item2, cover.Item3);
      var destination = Path.Combine(root.Path, $"planned-{cover.Item1}.m4b");
      var runner = new RecordingRunner(new ProcessRunner());
      var result = await new FFmpegConversionExecutor(
        runner,
        new FFmpegCommandFactory(tools),
        new Mp4MetadataWriter(),
        Path.Combine(root.Path, $"jobs-{cover.Item1}")).ExecuteAsync(
          CreateTaggedPlan(source, coverPath, destination), CancellationToken.None);

      Assert.Equal(ExecutionStatus.Succeeded, result.Status);
      Assert.False(File.Exists(destination));
      var media = await new FFprobeMediaProbe(new ProcessRunner(), tools).ProbeAsync(result.TemporaryOutputPath!);
      Assert.Single(media.AudioStreams);
      Assert.Single(media.AttachedPictures);
      Assert.Equal(cover.Item2 == "mjpeg" ? "mjpeg" : "png", media.AttachedPictures[0].CodecName);
      Assert.Single(media.Chapters);
      Assert.Equal("Opening & Intro", media.Chapters[0].RawTags["title"]);
      Assert.InRange(media.Chapters[0].StartTime, 0m, 0.01m);
      Assert.InRange(media.Chapters[0].EndTime, 0.9m, 1.1m);
      Assert.Equal("Integration Book", media.FormatTags["title"]);
      Assert.Equal("Integration Author", media.FormatTags["artist"]);
      Assert.Equal("Integration Narrator", media.FormatTags["composer"]);
    }
  }

  [PinnedMediaFact]
  public async Task SegmentedFinalMuxSelectsEmbeddedPictureFromLaterSource()
  {
    var tools = ResolveTools();
    using var root = new TemporaryDirectory();
    var sourceRoot = Directory.CreateDirectory(Path.Combine(root.Path, "sources")).FullName;
    var first = await GenerateAudioAsync(tools, Path.Combine(sourceRoot, "first.mp3"), "libmp3lame", "1", "44100", "440");
    var embedded = await GenerateEmbeddedAudioAsync(tools, Path.Combine(sourceRoot, "later-cover.m4a"), Path.Combine(sourceRoot, "cover.jpg"));
    var destination = Path.Combine(root.Path, "planned-embedded.m4b");
    var cover = new CoverCandidate("embedded", CoverOrigin.EmbeddedPicture, embedded, 1, "image/jpeg", 32, 32, CoverSemanticType.FrontCover, false);
    var metadata = new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
    var plan = new ConversionPlan([first, embedded], metadata, cover,
      [new ChapterEntry(0, 1_000_000, "First", "", "first.mp3"), new ChapterEntry(1_000_000, 2_000_000, "Later", "", "later-cover.m4a")],
      QualityProfileCatalog.Version1[2], ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, destination, AudioStrategy.SegmentedTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 2, "integration", "GenericMp4", 44100, 1);
    var runner = new RecordingRunner(new ProcessRunner());
    var result = await new FFmpegConversionExecutor(runner, new FFmpegCommandFactory(tools), new Mp4MetadataWriter(), Path.Combine(root.Path, "jobs"))
      .ExecuteAsync(plan, CancellationToken.None);

    Assert.Equal(ExecutionStatus.Succeeded, result.Status);
    var media = await new FFprobeMediaProbe(new ProcessRunner(), tools).ProbeAsync(result.TemporaryOutputPath!);
    Assert.Single(media.AudioStreams);
    Assert.Single(media.AttachedPictures);
    Assert.Equal("mjpeg", media.AttachedPictures[0].CodecName);
    var final = runner.Specs.Last(spec => spec.Arguments.Contains("copy") && spec.FileName.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
    var mapIndex = final.Arguments.ToList().IndexOf("-map", final.Arguments.ToList().IndexOf("-map") + 1);
    Assert.Equal("2:1", final.Arguments[mapIndex + 1]);
  }

  [PinnedMediaFact]
  public async Task NickMp3tagMetadataWriterPreservesAudioPacketsAndWritesMappedTags()
  {
    var tools = ResolveTools();
    using var root = new TemporaryDirectory();
    var source = await GenerateAudioAsync(tools, Path.Combine(root.Path, "spoken.mp3"), "libmp3lame", "1", "44100", "440");
    var fields = new Dictionary<SemanticField, AggregatedValue>
    {
      [SemanticField.BookTitle] = Value(SemanticField.BookTitle, "Nick Book"),
      [SemanticField.Author] = Value(SemanticField.Author, "Nick Author"),
      [SemanticField.Narrator] = Value(SemanticField.Narrator, "Nick Narrator"),
    };
    var metadata = new BookMetadata(fields, new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
    var destination = Path.Combine(root.Path, "planned-nick.m4b");
    var plan = new ConversionPlan([source], metadata, null, [new ChapterEntry(0, 1_000_000, "Opening", "", "spoken.mp3")], QualityProfileCatalog.Version1[2], ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, destination, AudioStrategy.DirectTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 1, "integration", "NickMp3tag", 44100, 1);
    var writer = new ObservingMetadataWriter(tools, new Mp4MetadataWriter());
    var result = await new FFmpegConversionExecutor(new RecordingRunner(new ProcessRunner()), new FFmpegCommandFactory(tools), writer, Path.Combine(root.Path, "jobs-nick"))
      .ExecuteAsync(plan, CancellationToken.None);

    Assert.Equal(ExecutionStatus.Succeeded, result.Status);
    Assert.Equal(writer.BeforeAudioHash, writer.AfterAudioHash);
    var media = await new FFprobeMediaProbe(new ProcessRunner(), tools).ProbeAsync(result.TemporaryOutputPath!);
    Assert.Equal("Nick Book", media.FormatTags["title"]);
    Assert.Equal("Nick Author", media.FormatTags["artist"]);
    Assert.Equal("Nick Narrator", media.FormatTags["composer"]);
  }

  private static ConversionPlan CreatePlan(AudioStrategy strategy, IReadOnlyList<string> sources, string destination, string sourceRoot)
  {
    var chapters = sources.Select((path, index) => new ChapterEntry(index * 1_000_000L, (index + 1) * 1_000_000L, $"Chapter {index + 1}", "", Path.GetRelativePath(sourceRoot, path))).ToArray();
    var metadata = new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
    return new ConversionPlan(sources, metadata, null, chapters, QualityProfileCatalog.Version1[2], ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, destination, strategy, [], new SpaceEstimate(1, 1, 1, 1, 0), 2, "integration", "GenericMp4");
  }

  private static ConversionPlan CreateTaggedPlan(string source, string cover, string destination)
  {
    var fields = new Dictionary<SemanticField, AggregatedValue>
    {
      [SemanticField.BookTitle] = Value(SemanticField.BookTitle, "Integration Book"),
      [SemanticField.Author] = Value(SemanticField.Author, "Integration Author"),
      [SemanticField.Narrator] = Value(SemanticField.Narrator, "Integration Narrator"),
    };
    var metadata = new BookMetadata(fields, new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
    var coverCandidate = new CoverCandidate("generated", CoverOrigin.ExternalFile, cover, null, Path.GetExtension(cover) == ".png" ? "image/png" : "image/jpeg", 32, 32, CoverSemanticType.FrontCover, true);
    return new ConversionPlan([source], metadata, coverCandidate, [new ChapterEntry(0, 1_000_000, "Opening & Intro", "", Path.GetFileName(source))], QualityProfileCatalog.Version1[2], ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, destination, AudioStrategy.DirectTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 1, "integration", "GenericMp4");
  }

  private static AggregatedValue Value(SemanticField field, string value) => new(field, AggregationState.Consistent, value, []);

  private static string ValueAfter(ProcessSpec spec, string argument)
  {
    var index = spec.Arguments.ToList().IndexOf(argument);
    return index >= 0 && index + 1 < spec.Arguments.Count ? spec.Arguments[index + 1] : "";
  }

  private static async Task<string> GenerateAudioAsync(MediaToolSet tools, string path, string codec, string channels, string rate, string frequency)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var args = new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency={frequency}:duration=1", "-ac", channels, "-ar", rate, "-c:a", codec };
    if (codec == "aac") args = [.. args, "-b:a", "96k"];
    var result = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFmpegPath, [.. args, path]), null, CancellationToken.None);
    Assert.Equal(0, result.ExitCode);
    return path;
  }

  private static async Task GenerateImageAsync(MediaToolSet tools, string path, string codec, string color)
  {
    var result = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"color=c={color}:s=32x32:d=1", "-frames:v", "1", "-c:v", codec, path]), null, CancellationToken.None);
    Assert.Equal(0, result.ExitCode);
  }

  private static async Task<string> GenerateEmbeddedAudioAsync(MediaToolSet tools, string path, string imagePath)
  {
    await GenerateImageAsync(tools, imagePath, "mjpeg", "red");
    var result = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=550:duration=1", "-i", imagePath, "-map", "0:a:0", "-map", "1:v:0", "-c:a", "aac", "-b:a", "96k", "-ar", "44100", "-ac", "1", "-c:v", "copy", "-disposition:v:0", "attached_pic", path]), null, CancellationToken.None);
    Assert.Equal(0, result.ExitCode);
    return path;
  }

  private sealed class ObservingMetadataWriter(MediaToolSet tools, IMp4MetadataWriter inner) : IMp4MetadataWriter
  {
    public string? BeforeAudioHash { get; private set; }
    public string? AfterAudioHash { get; private set; }
    public void Write(string path, IReadOnlyDictionary<string, string> tags)
    {
      BeforeAudioHash = AudioHash(path);
      inner.Write(path, tags);
      AfterAudioHash = AudioHash(path);
    }

    private string AudioHash(string path)
    {
      var result = new ProcessRunner().RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-i", path, "-map", "0:a:0", "-f", "hash", "-hash", "sha256", "-"]), null, CancellationToken.None).GetAwaiter().GetResult();
      Assert.Equal(0, result.ExitCode);
      return result.StandardOutput.Trim();
    }
  }

  private static MediaToolSet ResolveTools()
  {
    var directory = Environment.GetEnvironmentVariable("AUDIOBOOKCONVERTER_FFMPEG_DIR");
    if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "ffmpeg.exe"))) throw Xunit.Sdk.SkipException.ForSkip("Pinned FFmpeg tools are not available; set AUDIOBOOKCONVERTER_FFMPEG_DIR to run real-media integration tests.");
    return new MediaToolLocator(directory).Resolve();
  }

  private sealed class RecordingRunner(IProcessRunner inner) : IProcessRunner
  {
    private readonly object _sync = new();
    public List<ProcessSpec> Specs { get; } = [];
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken)
    {
      lock (_sync) Specs.Add(spec);
      return await inner.RunAsync(spec, progress, cancellationToken);
    }
  }

  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("audiobookconverter-ffmpeg-integration-").FullName;
    public string Path { get; }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
  }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PinnedMediaFactAttribute : FactAttribute
{
  public PinnedMediaFactAttribute()
  {
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUDIOBOOKCONVERTER_FFMPEG_DIR")))
      Skip = "Pinned FFmpeg tools are not available; set AUDIOBOOKCONVERTER_FFMPEG_DIR to run real-media integration tests.";
  }
}
