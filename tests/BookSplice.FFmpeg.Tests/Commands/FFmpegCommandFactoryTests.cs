using BookSplice.Core.Chapters;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.FFmpeg.Commands;
using BookSplice.FFmpeg.Tools;
using BookSplice.Core.Covers;

namespace BookSplice.FFmpeg.Tests.Commands;

public sealed class FFmpegCommandFactoryTests
{
  [Fact]
  public void CreateFinalUsesArgumentListAndNativeAacForDirectTranscode()
  {
    var spec = Factory().CreateFinal(Plan(AudioStrategy.DirectTranscode), @"C:\temp\out.m4b");
    Assert.Equal("ffmpeg.exe", spec.FileName);
    Assert.Contains("-nostdin", spec.Arguments);
    Assert.Equal("pipe:1", spec.Arguments[spec.Arguments.ToList().IndexOf("-progress") + 1]);
    Assert.Equal("aac", spec.Arguments[spec.Arguments.ToList().IndexOf("-c:a") + 1]);
    Assert.Equal("96k", spec.Arguments[spec.Arguments.ToList().IndexOf("-b:a") + 1]);
    Assert.Contains(@"C:\input\one.mp3", spec.Arguments);
  }

  [Fact]
  public void CreateFinalStreamCopyDoesNotSelectAnAudioEncoder()
  {
    var spec = Factory().CreateFinal(Plan(AudioStrategy.AacStreamCopy), @"C:\temp\out.m4b", @"C:\temp\concat.txt");
    Assert.Equal("copy", spec.Arguments[spec.Arguments.ToList().IndexOf("-c:a") + 1]);
    Assert.DoesNotContain("aac_low", spec.Arguments);
  }

  [Fact]
  public void CreateFinalOneSourceStreamCopyMapsTheSourceDirectly()
  {
    var spec = Factory().CreateFinal(Plan(AudioStrategy.AacStreamCopy), @"C:\temp\out.m4b");
    Assert.Contains(@"C:\input\one.mp3", spec.Arguments);
    Assert.DoesNotContain("concat.txt", spec.Arguments);
    Assert.DoesNotContain("-safe", spec.Arguments);
  }

  [Fact]
  public void CreateFinalEmbeddedCoverMapsTheExtractedFileAndNeverTheSourcePicture()
  {
    var cover = new CoverCandidate("hash", CoverOrigin.EmbeddedPicture, @"C:\input\later.mp3", 3, "image/jpeg", 1, 1, CoverSemanticType.FrontCover, false);
    var spec = Factory().CreateFinal(Plan(AudioStrategy.AacStreamCopy, 2, cover: cover), @"C:\temp\out.m4b", @"C:\temp\concat.txt", coverFile: @"C:\temp\cover.jpg");
    var inputs = spec.Arguments.Select((value, index) => (value, index)).Where(item => item.value == "-i").Select(item => spec.Arguments[item.index + 1]).ToArray();
    Assert.Equal([@"C:\temp\concat.txt", @"C:\temp\cover.jpg"], inputs);
    Assert.DoesNotContain(@"C:\input\later.mp3", spec.Arguments);
    var mapIndex = spec.Arguments.ToList().IndexOf("-map", spec.Arguments.ToList().IndexOf("-map") + 1);
    Assert.Equal("1:v:0", spec.Arguments[mapIndex + 1]);
    Assert.DoesNotContain("1:3", spec.Arguments);
  }

  [Fact]
  public void CreateFinalEmbeddedCoverRequiresTheExtractedFile()
  {
    var cover = new CoverCandidate("hash", CoverOrigin.EmbeddedPicture, @"C:\input\later.mp3", 3, "image/jpeg", 1, 1, CoverSemanticType.FrontCover, false);
    Assert.Throws<ArgumentException>(() => Factory().CreateFinal(Plan(AudioStrategy.DirectTranscode, cover: cover), @"C:\temp\out.m4b"));
  }

  [Fact]
  public void CreateCoverExtractionCopiesTheSelectedPictureWithTheSameArgumentsTheCoverHashWasTaken()
  {
    var cover = new CoverCandidate("hash", CoverOrigin.EmbeddedPicture, @"C:\input\later.mp3", 3, "image/jpeg", 1, 1, CoverSemanticType.FrontCover, false);
    var spec = Factory().CreateCoverExtraction(cover, @"C:\temp\cover.jpg");
    Assert.Equal(["-hide_banner", "-loglevel", "error", "-nostdin", "-i", @"C:\input\later.mp3", "-map", "0:3", "-c:v", "copy", "-frames:v", "1", "-f", "image2pipe", "-y", @"C:\temp\cover.jpg"], spec.Arguments);
    Assert.Throws<ArgumentException>(() => Factory().CreateCoverExtraction(cover with { EmbeddedPictureIndex = null }, @"C:\temp\cover.jpg"));
  }

  [Theory]
  [InlineData("cover.jpg", "image/jpeg")]
  [InlineData("cover.png", "image/png")]
  public void CreateFinalExternalCoverAddsDedicatedInput(string path, string contentType)
  {
    var cover = new CoverCandidate("hash", CoverOrigin.ExternalFile, $@"C:\covers\{path}", null, contentType, 1, 1, CoverSemanticType.FrontCover, true);
    var spec = Factory().CreateFinal(Plan(AudioStrategy.DirectTranscode, cover: cover), @"C:\temp\out.m4b");
    Assert.Contains(cover.SourcePath, spec.Arguments);
    var mapIndex = spec.Arguments.ToList().IndexOf("-map", spec.Arguments.ToList().IndexOf("-map") + 1);
    Assert.Equal("1:v:0", spec.Arguments[mapIndex + 1]);
  }

  [Theory]
  [InlineData("image/jpeg", 1)]
  [InlineData("image/png", 4)]
  public void CreateFinalEmbeddedCoverIgnoresThePictureIndexOfTheSource(string contentType, int pictureIndex)
  {
    var cover = new CoverCandidate("hash", CoverOrigin.EmbeddedPicture, @"C:\input\later1.mp3", pictureIndex, contentType, 1, 1, CoverSemanticType.FrontCover, false);
    var spec = Factory().CreateFinal(Plan(AudioStrategy.DirectTranscode, cover: cover), @"C:\temp\out.m4b", coverFile: @"C:\temp\cover.img");
    var mapIndex = spec.Arguments.ToList().IndexOf("-map", spec.Arguments.ToList().IndexOf("-map") + 1);
    Assert.Equal("1:v:0", spec.Arguments[mapIndex + 1]);
    var inputIndexes = spec.Arguments.Select((value, index) => (value, index)).Where(item => item.value == "-i").Select(item => item.index).ToArray();
    Assert.Equal(@"C:\temp\cover.img", spec.Arguments[inputIndexes[^1] + 1]);
    Assert.DoesNotContain(cover.SourcePath, spec.Arguments);
  }

  [Fact]
  public void CreateFinalFilterUsesDistinctFilterFileArgument()
  {
    var filterPath = @"C:\temp\filter.txt";
    var spec = Factory().CreateFinal(Plan(AudioStrategy.FilterConcatTranscode, 2), @"C:\temp\out.m4b", filterGraph: filterPath);
    var index = spec.Arguments.ToList().IndexOf("-/filter_complex");
    Assert.True(index >= 0);
    Assert.Equal(filterPath, spec.Arguments[index + 1]);
    Assert.DoesNotContain("[0:a]", spec.Arguments);
  }

  [Fact]
  public void MeasureDecodesThroughTheEncodersSourceChain()
  {
    var spec = Factory().CreateMeasure(Plan(AudioStrategy.SegmentedTranscode, sampleRate: 48000, channels: 1), @"C:\input\one.mp3");
    Assert.Equal(["-hide_banner", "-loglevel", "verbose", "-nostdin", "-i", @"C:\input\one.mp3", "-map", "0:a:0", "-vn", "-af", "aresample=48000,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=mono", "-c:a", "pcm_s16le", "-f", "null", "-"], spec.Arguments);
  }

  [Fact]
  public void RangeEncodesTheTrimmedConcatAsAdtsWithFrozenSampleRateAndChannels()
  {
    var spec = Factory().CreateRange(Plan(AudioStrategy.SegmentedTranscode, sampleRate: 48000, channels: 1), [@"C:\input\one.mp3", @"C:\input\two.mp3"], 1000, 5000, @"C:\temp\segment.aac");
    var arguments = spec.Arguments.ToList();
    Assert.Equal(["-i", @"C:\input\one.mp3", "-i", @"C:\input\two.mp3"], arguments.Skip(7).Take(4));
    var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
    Assert.Equal("[0:a]aresample=48000,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=mono[a0];[1:a]aresample=48000,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=mono[a1];[a0][a1]concat=n=2:v=0:a=1[cat];[cat]atrim=start_sample=1000:end_sample=5000,asetpts=N/SR/TB[aout]", graph);
    Assert.Equal("[aout]", arguments[arguments.IndexOf("-map") + 1]);
    Assert.Equal("48000", arguments[arguments.IndexOf("-ar") + 1]);
    Assert.Equal("1", arguments[arguments.IndexOf("-ac") + 1]);
    Assert.Equal(["-f", "adts", "-y", @"C:\temp\segment.aac"], arguments.TakeLast(4));
  }

  [Fact]
  public void RangeOfOneInputSkipsTheConcat()
  {
    Assert.Equal("[0:a]aresample=44100,aformat=sample_fmts=fltp:sample_rates=44100:channel_layouts=stereo[a0];[a0]atrim=start_sample=0:end_sample=900,asetpts=N/SR/TB[aout]", FilterGraphWriter.WriteRange(1, 44_100, 2, 0, 900));
  }

  [Theory]
  [InlineData(44_100, "-0.023220")]
  [InlineData(48_000, "-0.021333")]
  public void IntermediatePutsThePrimingFrameBeforeZeroAndCopiesIntoAnMp4(int sampleRate, string offset)
  {
    var spec = Factory().CreateIntermediate(Plan(AudioStrategy.SegmentedTranscode, sampleRate: sampleRate), @"C:\temp\joined.aac", @"C:\temp\intermediate.m4a");
    Assert.Equal(["-itsoffset", offset, "-i", @"C:\temp\joined.aac", "-c", "copy", "-f", "ipod", "-y", @"C:\temp\intermediate.m4a"], spec.Arguments.Skip(7));
  }

  [Fact]
  public void FilterGraphSupportsTheThirtyTwoFilePlanningBoundaryAndNormalizesFormat()
  {
    var graph = FilterGraphWriter.Write(32, 48_000, 1);
    Assert.Contains("[31:a]", graph);
    Assert.Contains("sample_fmts=fltp:sample_rates=48000:channel_layouts=mono", graph);
    Assert.Contains("concat=n=32:v=0:a=1[aout]", graph);
    Assert.DoesNotContain("C:\\", graph);
  }

  [Fact]
  public void CreateFinalProducesCompleteArgumentArraysForAllStrategies()
  {
    var prefix = new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1", "-nostats" };
    Assert.Equal(prefix.Concat(["-i", @"C:\input\one.mp3", "-map", "0:a:0", "-c:a", "copy", "-movflags", "+faststart", "-f", "ipod", "-y", @"C:\temp\copy-one.m4b"]), Factory().CreateFinal(Plan(AudioStrategy.AacStreamCopy), @"C:\temp\copy-one.m4b").Arguments);
    Assert.Equal(prefix.Concat(["-f", "concat", "-safe", "0", "-i", @"C:\temp\concat.txt", "-map", "0:a:0", "-c:a", "copy", "-movflags", "+faststart", "-f", "ipod", "-y", @"C:\temp\copy-many.m4b"]), Factory().CreateFinal(Plan(AudioStrategy.AacStreamCopy, 2), @"C:\temp\copy-many.m4b", @"C:\temp\concat.txt").Arguments);
    Assert.Equal(prefix.Concat(["-i", @"C:\input\one.mp3", "-map", "0:a:0", "-c:a", "aac", "-profile:a", "aac_low", "-b:a", "96k", "-ar", "44100", "-ac", "2", "-movflags", "+faststart", "-f", "ipod", "-y", @"C:\temp\direct.m4b"]), Factory().CreateFinal(Plan(AudioStrategy.DirectTranscode), @"C:\temp\direct.m4b").Arguments);
    Assert.Equal(prefix.Concat(["-i", @"C:\input\one.mp3", "-i", @"C:\input\later1.mp3", "-/filter_complex", @"C:\temp\filter.txt", "-map", "[aout]", "-c:a", "aac", "-profile:a", "aac_low", "-b:a", "96k", "-ar", "44100", "-ac", "2", "-movflags", "+faststart", "-f", "ipod", "-y", @"C:\temp\filter.m4b"]), Factory().CreateFinal(Plan(AudioStrategy.FilterConcatTranscode, 2), @"C:\temp\filter.m4b", filterGraph: @"C:\temp\filter.txt").Arguments);
    Assert.Equal(prefix.Concat(["-i", @"C:\temp\intermediate.m4a", "-map", "0:a:0", "-c:a", "copy", "-movflags", "+faststart", "-f", "ipod", "-y", @"C:\temp\segmented.m4b"]), Factory().CreateFinal(Plan(AudioStrategy.SegmentedTranscode, 2), @"C:\temp\segmented.m4b", intermediateAudio: @"C:\temp\intermediate.m4a").Arguments);
    Assert.Throws<ArgumentException>(() => Factory().CreateFinal(Plan(AudioStrategy.SegmentedTranscode, 2), @"C:\temp\segmented.m4b"));
  }

  private static FFmpegCommandFactory Factory() => new(new MediaToolSet("ffmpeg.exe", "ffprobe.exe", "pinned", "pinned"));
  private static ConversionPlan Plan(AudioStrategy strategy, int count = 1, int sampleRate = 44100, int channels = 2, CoverCandidate? cover = null)
  {
    var sources = Enumerable.Range(0, count).Select(index => index == 0 ? @"C:\input\one.mp3" : $@"C:\input\later{index}.mp3").ToArray();
    var chapters = sources.Select((_, index) => new ChapterEntry(index * 1_000_000L, (index + 1) * 1_000_000L, $"Chapter {index + 1}", "", sources[index])).ToArray();
    return new ConversionPlan(sources, new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()), cover, chapters, QualityProfileCatalog.Version1[2], ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, @"C:\destination\book.m4b", strategy, Array.Empty<string>(), new SpaceEstimate(1, 1, 1, 1, 0), 1, "test", MetadataProfiles.GenericMp4, sampleRate, channels);
  }
}
