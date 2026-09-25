using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.FFmpeg.Commands;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Tools;
using BookSplice.FFmpeg.Validation;

namespace BookSplice.FFmpeg.Tests.Execution;

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
      Assert.Equal("Integration Author", media.FormatTags["album_artist"]);
      Assert.Equal("Integration Narrator", media.FormatTags["composer"]);
    }
  }

  [PinnedMediaFact]
  public async Task MetadataWritePreservesSparseLongTimelineAndFinalChapter()
  {
    var tools = ResolveTools();
    using var root = new TemporaryDirectory();
    var metadata = Path.Combine(root.Path, "long-timeline.ffmeta");
    var output = Path.Combine(root.Path, "long-timeline.m4b");
    File.WriteAllText(metadata, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000000\nSTART=0\nEND=49999000000\ntitle=Opening\n[CHAPTER]\nTIMEBASE=1/1000000\nSTART=49999000000\nEND=50001000000\ntitle=Final\n", new System.Text.UTF8Encoding(false));
    var generated = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFmpegPath,
      ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-f", "ffmetadata", "-i", metadata,
       "-map", "0:a:0", "-map_metadata", "1", "-map_chapters", "1", "-filter:a", "asetpts=PTS+50000/TB", "-c:a", "aac", "-b:a", "96k", "-ar", "44100", "-ac", "2", "-movflags", "+faststart", "-f", "ipod", output]), null, CancellationToken.None);
    Assert.Equal(0, generated.ExitCode);

    var probe = new FFprobeMediaProbe(new ProcessRunner(), tools);
    var before = await probe.ProbeAsync(output);
    new Mp4MetadataWriter().Write(output, new Dictionary<string, string> { ["TITLE"] = "Generated long timeline" });
    var after = await probe.ProbeAsync(output);

    Assert.Equal(49_999m, before.Chapters[0].EndTime);
    Assert.Equal(49_999m, before.Chapters[1].StartTime);
    Assert.Equal(before.Duration, after.Duration);
    Assert.Equal(before.Chapters.Select(chapter => (chapter.Id, chapter.StartTime, chapter.EndTime, Title: chapter.RawTags.GetValueOrDefault("title", ""))), after.Chapters.Select(chapter => (chapter.Id, chapter.StartTime, chapter.EndTime, Title: chapter.RawTags.GetValueOrDefault("title", ""))));
    Assert.Equal(2, after.Chapters.Count);
  }
  [PinnedMediaFact]
  public async Task LongFormFilterConcatPreservesAllGeneratedChaptersAfterMetadataWrite()
  {
    var tools = ResolveTools();
    using var root = new TemporaryDirectory();
    var sourceRoot = Directory.CreateDirectory(Path.Combine(root.Path, "sources")).FullName;
    var sources = await Task.WhenAll(Enumerable.Range(1, 10).Select(index =>
      GenerateAudioAsync(tools, Path.Combine(sourceRoot, $"{index:D2}.mp3"), "libmp3lame", "1", "44100", (400 + index * 10).ToString(System.Globalization.CultureInfo.InvariantCulture), "240")));
    var destination = Path.Combine(root.Path, "planned-multi-chapter.m4b");
    var probe = new FFprobeMediaProbe(new ProcessRunner(), tools);
    var chapters = new List<ChapterEntry>();
    long cursor = 0;
    foreach (var source in sources)
    {
      var facts = await probe.ProbeAsync(source);
      var duration = checked((long)decimal.Round(facts.Duration!.Value * 1_000_000m, 0, MidpointRounding.ToEven));
      chapters.Add(new ChapterEntry(cursor, checked(cursor + duration), $"Chapter {chapters.Count + 1}", "", Path.GetRelativePath(sourceRoot, source)));
      cursor = checked(cursor + duration);
    }
    var writer = new ObservingMetadataWriter(tools, new Mp4MetadataWriter());
    var result = await new FFmpegConversionExecutor(
      new ProcessRunner(),
      new FFmpegCommandFactory(tools),
      writer,
      Path.Combine(root.Path, "jobs-multi-chapter")).ExecuteAsync(
        CreatePlan(AudioStrategy.FilterConcatTranscode, sources, destination, sourceRoot, chapters), CancellationToken.None);

    Assert.True(result.Status == ExecutionStatus.Succeeded, writer.Failure?.ToString());
    Assert.Equal(sources.Length, writer.BeforeChapterCount);
    Assert.Equal(sources.Length, writer.AfterChapterCount);
    var media = await new FFprobeMediaProbe(new ProcessRunner(), tools).ProbeAsync(result.TemporaryOutputPath!);
    Assert.Equal(sources.Length, media.Chapters.Count);
    Assert.All(media.Chapters, chapter => Assert.True(chapter.EndTime > chapter.StartTime));
    Assert.All(media.Chapters.Zip(media.Chapters.Skip(1)), pair => Assert.True(pair.Second.StartTime >= pair.First.EndTime));
    var report = await new FFmpegOutputValidator(probe, new ProcessRunner(), tools, new CoverPayloadValidator())
      .ValidateAsync(CreatePlan(AudioStrategy.FilterConcatTranscode, sources, destination, sourceRoot, chapters), result.TemporaryOutputPath!, CancellationToken.None);
    Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Checks.Where(check => !check.Passed).Select(check => $"{check.Code}: {check.Message}")));
  }
  [PinnedMediaFact]
  public async Task FilterConcatPreservesChapterTimingForMixedMp3StartTimestamps()
  {
    var tools = ResolveTools();
    using var root = new TemporaryDirectory();
    var sourceRoot = Directory.CreateDirectory(Path.Combine(root.Path, "mixed-start-sources")).FullName;
    var sources = new List<string>();
    for (var index = 1; index <= 10; index++)
    {
      sources.Add(await GenerateAudioAsync(
        tools,
        Path.Combine(sourceRoot, $"{index:D2}.mp3"),
        "libmp3lame",
        "2",
        "44100",
        (500 + index * 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
        index == 10 ? "0.05" : "8",
        writeXing: index % 2 != 0,
        quality: index % 2 == 0 ? "9" : null));
    }

    var probe = new FFprobeMediaProbe(new ProcessRunner(), tools);
    var chapters = new List<ChapterEntry>();
    var starts = new List<decimal>();
    long cursor = 0;
    foreach (var source in sources)
    {
      var facts = await probe.ProbeAsync(source);
      starts.Add(facts.AudioStreams.Single().StartTime ?? throw new InvalidOperationException("The generated MP3 omitted its start timestamp."));
      var duration = checked((long)decimal.Round(facts.Duration!.Value * 1_000_000m, 0, MidpointRounding.ToEven));
      chapters.Add(new ChapterEntry(cursor, checked(cursor + duration), $"Mixed chapter {chapters.Count + 1}", "", Path.GetRelativePath(sourceRoot, source)));
      cursor = checked(cursor + duration);
    }
    Assert.Contains(starts, start => start == 0m);
    Assert.Contains(starts, start => start > 0m);

    var destination = Path.Combine(root.Path, "planned-mixed-starts.m4b");
    var plan = CreatePlan(AudioStrategy.FilterConcatTranscode, sources, destination, sourceRoot, chapters, ValidationLevel.Full);
    var writer = new ObservingMetadataWriter(tools, new Mp4MetadataWriter());
    var result = await new FFmpegConversionExecutor(
      new ProcessRunner(),
      new FFmpegCommandFactory(tools),
      writer,
      Path.Combine(root.Path, "jobs-mixed-starts")).ExecuteAsync(plan, CancellationToken.None);

    Assert.True(result.Status == ExecutionStatus.Succeeded, writer.Failure?.ToString());
    Assert.Equal(sources.Count, writer.BeforeChapters?.Length);
    Assert.Equal(sources.Count, writer.AfterChapters?.Length);
    Assert.Equal(writer.BeforeChapters, writer.AfterChapters);
    Assert.Equal(sources.Count, writer.AfterChapters!.Select(chapter => chapter.Id).Distinct().Count());

    var media = await probe.ProbeAsync(result.TemporaryOutputPath!);
    Assert.Equal(sources.Count, media.Chapters.Count);
    Assert.Equal(sources.Count, media.Chapters.Select(chapter => chapter.Id).Distinct().Count());
    Assert.Equal(chapters.Select(chapter => chapter.Title), media.Chapters.Select(chapter => chapter.RawTags["title"]));
    Assert.All(media.Chapters, chapter => Assert.True(chapter.EndTime > chapter.StartTime));
    Assert.All(media.Chapters.Zip(media.Chapters.Skip(1)), pair => Assert.True(pair.Second.StartTime >= pair.First.EndTime));

    var report = await new FFmpegOutputValidator(probe, new ProcessRunner(), tools, new CoverPayloadValidator())
      .ValidateAsync(plan, result.TemporaryOutputPath!, CancellationToken.None);
    Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Checks.Where(check => !check.Passed).Select(check => $"{check.Code}: {check.Message}")));
    Assert.Contains(report.Checks, check => check.Code == "decode.full" && check.Passed);
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
  [PinnedMediaFact]
  public async Task NickMp3tagOutputPassesFullProfileAwareValidation()
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
    var metadata = new BookMetadata(fields, new Dictionary<string, string> { ["CUSTOM"] = "Preserved" }, new Dictionary<SemanticField, IReadOnlyList<string>>());
    var destination = Path.Combine(root.Path, "planned-nick.m4b");
    var plan = new ConversionPlan([source], metadata, null, [new ChapterEntry(0, 1_000_000, "Opening", "", "spoken.mp3")], QualityProfileCatalog.Version1[2], ValidationLevel.Full, CollisionPolicy.AvoidCollision, destination, AudioStrategy.DirectTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 1, "integration", "NickMp3tag", 44100, 1);
    var runner = new ProcessRunner();
    var result = await new FFmpegConversionExecutor(runner, new FFmpegCommandFactory(tools), new Mp4MetadataWriter(), Path.Combine(root.Path, "jobs-nick-validation"))
      .ExecuteAsync(plan, CancellationToken.None);

    Assert.Equal(ExecutionStatus.Succeeded, result.Status);
    var probe = new FFprobeMediaProbe(runner, tools);
    var facts = await probe.ProbeAsync(result.TemporaryOutputPath!);
    var report = await new FFmpegOutputValidator(probe, runner, tools, new CoverPayloadValidator())
      .ValidateAsync(plan, result.TemporaryOutputPath!, CancellationToken.None);

    Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Checks.Where(check => !check.Passed).Select(check => $"{check.Code}: {check.Message}")) + Environment.NewLine + string.Join(", ", facts.FormatTags.Select(tag => $"{tag.Key}={tag.Value}")));
  }

  private static ConversionPlan CreatePlan(AudioStrategy strategy, IReadOnlyList<string> sources, string destination, string sourceRoot) =>
    CreatePlan(strategy, sources, destination, sourceRoot, sources.Select((path, index) => new ChapterEntry(index * 1_000_000L, (index + 1) * 1_000_000L, $"Chapter {index + 1}", "", Path.GetRelativePath(sourceRoot, path))).ToArray());

  private static ConversionPlan CreatePlan(AudioStrategy strategy, IReadOnlyList<string> sources, string destination, string sourceRoot, IReadOnlyList<ChapterEntry> chapters, ValidationLevel validationLevel = ValidationLevel.Lightweight)
  {
    var metadata = new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
    return new ConversionPlan(sources, metadata, null, chapters, QualityProfileCatalog.Version1[2], validationLevel, CollisionPolicy.AvoidCollision, destination, strategy, [], new SpaceEstimate(1, 1, 1, 1, 0), 2, "integration", "GenericMp4");
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

  private static async Task<string> GenerateAudioAsync(MediaToolSet tools, string path, string codec, string channels, string rate, string frequency, string duration = "1", bool writeXing = true, string? containerFormat = null, string? quality = null)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var args = new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency={frequency}:duration={duration}", "-ac", channels, "-ar", rate, "-c:a", codec };
    if (codec == "aac") args = [.. args, "-b:a", "96k"];
    if (codec == "libmp3lame" && quality is not null) args = [.. args, "-q:a", quality];
    if (codec == "libmp3lame" && !writeXing) args = [.. args, "-write_xing", "0"];
    if (containerFormat is not null) args = [.. args, "-f", containerFormat];
    var generatorDirectory = codec == "libmp3lame" ? Environment.GetEnvironmentVariable("BOOKSPLICE_FIXTURE_FFMPEG_DIR") : null;
    var generatorPath = string.IsNullOrWhiteSpace(generatorDirectory) ? tools.FFmpegPath : new MediaToolLocator(generatorDirectory).Resolve().FFmpegPath;
    var result = await new ProcessRunner().RunAsync(new ProcessSpec(generatorPath, [.. args, path]), null, CancellationToken.None);
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
    public ChapterSnapshot[]? BeforeChapters { get; private set; }
    public ChapterSnapshot[]? AfterChapters { get; private set; }
    public int? BeforeChapterCount => BeforeChapters?.Length;
    public int? AfterChapterCount => AfterChapters?.Length;
    public Exception? Failure { get; private set; }
    public void Write(string path, IReadOnlyDictionary<string, string> tags)
    {
      BeforeAudioHash = AudioHash(path);
      BeforeChapters = Chapters(path);
      try { inner.Write(path, tags); }
      catch (Exception exception) { Failure = exception; throw; }
      AfterAudioHash = AudioHash(path);
      AfterChapters = Chapters(path);
    }

    private string AudioHash(string path)
    {
      var result = new ProcessRunner().RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-i", path, "-map", "0:a:0", "-f", "hash", "-hash", "sha256", "-"]), null, CancellationToken.None).GetAwaiter().GetResult();
      Assert.Equal(0, result.ExitCode);
      return result.StandardOutput.Trim();
    }

    private ChapterSnapshot[] Chapters(string path) => new FFprobeMediaProbe(new ProcessRunner(), tools).ProbeAsync(path).GetAwaiter().GetResult().Chapters
      .Select(chapter => new ChapterSnapshot(chapter.Id, chapter.StartTime, chapter.EndTime, chapter.RawTags.GetValueOrDefault("title", "")))
      .ToArray();
  }

  private readonly record struct ChapterSnapshot(long Id, decimal StartTime, decimal EndTime, string Title);

  private static MediaToolSet ResolveTools()
  {
    var directory = Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR");
    if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "ffmpeg.exe"))) throw Xunit.Sdk.SkipException.ForSkip("Pinned FFmpeg tools are not available; set BOOKSPLICE_FFMPEG_DIR to run real-media integration tests.");
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
    private const string Prefix = "booksplice-acceptance-";
    private const string Marker = ".booksplice-acceptance-owned";
    private readonly string _baseRoot;
    private readonly string _token;

    public TemporaryDirectory()
    {
      _baseRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
      _token = Guid.NewGuid().ToString("N");
      Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(_baseRoot, Prefix + Guid.NewGuid().ToString("N")));
      if (!IsContained(_baseRoot, Path)) throw new IOException("The generated test directory escaped the temporary root.");
      Directory.CreateDirectory(Path);
      File.WriteAllText(System.IO.Path.Combine(Path, Marker), _token, new System.Text.UTF8Encoding(false));
    }

    public string Path { get; }

    public void Dispose()
    {
      var resolved = System.IO.Path.GetFullPath(Path);
      if (!IsContained(_baseRoot, resolved) || !System.IO.Path.GetFileName(resolved).StartsWith(Prefix, StringComparison.Ordinal) ||
          System.IO.Path.GetFileName(resolved).Length != Prefix.Length + 32) throw new IOException("The generated test cleanup target was not owned.");
      if (!Directory.Exists(resolved)) return;
      if (HasReparsePoint(resolved)) throw new IOException("The generated test cleanup target contains a reparse point.");
      var marker = System.IO.Path.Combine(resolved, Marker);
      if (!File.Exists(marker) || !string.Equals(File.ReadAllText(marker).Trim(), _token, StringComparison.Ordinal))
        throw new IOException("The generated test cleanup token did not match.");
      Directory.Delete(resolved, true);
    }

    private static bool IsContained(string root, string candidate)
    {
      var relative = System.IO.Path.GetRelativePath(root, candidate);
      return !System.IO.Path.IsPathRooted(relative) && relative is not "." && relative is not ".." &&
        !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
        !relative.StartsWith(".." + System.IO.Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool HasReparsePoint(string root)
    {
      if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return true;
      return Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
    }
  }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PinnedMediaFactAttribute : FactAttribute
{
  public PinnedMediaFactAttribute()
  {
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR")))
      Skip = "Pinned FFmpeg tools are not available; set BOOKSPLICE_FFMPEG_DIR to run real-media integration tests.";
  }
}
