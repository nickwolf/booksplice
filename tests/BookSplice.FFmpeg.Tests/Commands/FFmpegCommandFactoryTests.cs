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
  public void CreateFinalEmbeddedCoverAddsDedicatedInputAndUsesEmbeddedIndex()
  {
    var cover = new CoverCandidate("hash", CoverOrigin.EmbeddedPicture, @"C:\input\later.mp3", 3, "image/jpeg", 1, 1, CoverSemanticType.FrontCover, false);
    var spec = Factory().CreateFinal(Plan(AudioStrategy.AacStreamCopy, 2, cover: cover), @"C:\temp\out.m4b", @"C:\temp\concat.txt");
    var inputs = spec.Arguments.Select((value, index) => (value, index)).Where(item => item.value == "-i").Select(item => spec.Arguments[item.index + 1]).ToArray();
    Assert.Equal([@"C:\temp\concat.txt", @"C:\input\later.mp3"], inputs);
    var mapIndex = spec.Arguments.ToList().IndexOf("-map", spec.Arguments.ToList().IndexOf("-map") + 1);
    Assert.Equal("1:3", spec.Arguments[mapIndex + 1]);
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
  public void CreateFinalEmbeddedCoverMapsSelectedPictureFromDedicatedInput(string contentType, int pictureIndex)
  {
    var cover = new CoverCandidate("hash", CoverOrigin.EmbeddedPicture, @"C:\input\later1.mp3", pictureIndex, contentType, 1, 1, CoverSemanticType.FrontCover, false);
    var spec = Factory().CreateFinal(Plan(AudioStrategy.DirectTranscode, cover: cover), @"C:\temp\out.m4b");
    var mapIndex = spec.Arguments.ToList().IndexOf("-map", spec.Arguments.ToList().IndexOf("-map") + 1);
    Assert.Equal($"1:{pictureIndex}", spec.Arguments[mapIndex + 1]);
    var inputIndexes = spec.Arguments.Select((value, index) => (value, index)).Where(item => item.value == "-i").Select(item => item.index).ToArray();
    Assert.Equal(cover.SourcePath, spec.Arguments[inputIndexes[^1] + 1]);
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
  public void CreateSegmentUsesFrozenSampleRateAndChannels()
  {
    var spec = Factory().CreateSegment(Plan(AudioStrategy.SegmentedTranscode, sampleRate: 48000, channels: 1), @"C:\input\one.mp3", @"C:\temp\segment.aac");
    Assert.Equal("48000", spec.Arguments[spec.Arguments.ToList().IndexOf("-ar") + 1]);
    Assert.Equal("1", spec.Arguments[spec.Arguments.ToList().IndexOf("-ac") + 1]);
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
    Assert.Equal(prefix.Concat(["-f", "concat", "-safe", "0", "-i", @"C:\temp\segments.txt", "-map", "0:a:0", "-c:a", "copy", "-movflags", "+faststart", "-f", "ipod", "-y", @"C:\temp\segmented.m4b"]), Factory().CreateFinal(Plan(AudioStrategy.SegmentedTranscode, 2), @"C:\temp\segmented.m4b", @"C:\temp\segments.txt").Arguments);
  }

  private static FFmpegCommandFactory Factory() => new(new MediaToolSet("ffmpeg.exe", "ffprobe.exe", "pinned", "pinned"));
  private static ConversionPlan Plan(AudioStrategy strategy, int count = 1, int sampleRate = 44100, int channels = 2, CoverCandidate? cover = null)
  {
    var sources = Enumerable.Range(0, count).Select(index => index == 0 ? @"C:\input\one.mp3" : $@"C:\input\later{index}.mp3").ToArray();
    var chapters = sources.Select((_, index) => new ChapterEntry(index * 1_000_000L, (index + 1) * 1_000_000L, $"Chapter {index + 1}", "", sources[index])).ToArray();
    return new ConversionPlan(sources, new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()), cover, chapters, QualityProfileCatalog.Version1[2], ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, @"C:\destination\book.m4b", strategy, Array.Empty<string>(), new SpaceEstimate(1, 1, 1, 1, 0), 1, "test", "GenericMp4", sampleRate, channels);
  }
}
