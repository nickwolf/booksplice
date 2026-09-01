using AudiobookConverter.Core.Chapters;
using AudiobookConverter.Core.Metadata;
using AudiobookConverter.Core.Naming;
using AudiobookConverter.Core.Planning;
using AudiobookConverter.Core.Settings;
using AudiobookConverter.FFmpeg.Commands;
using AudiobookConverter.FFmpeg.Tools;

namespace AudiobookConverter.FFmpeg.Tests.Commands;

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

  private static FFmpegCommandFactory Factory() => new(new MediaToolSet("ffmpeg.exe", "ffprobe.exe", "pinned", "pinned"));
  private static ConversionPlan Plan(AudioStrategy strategy) => new([@"C:\input\one.mp3"], new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()), null, Array.Empty<ChapterEntry>(), QualityProfileCatalog.Version1[2], ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, @"C:\destination\book.m4b", strategy, Array.Empty<string>(), new SpaceEstimate(1, 1, 1, 1, 0), 1, "test", "GenericMp4");
}
