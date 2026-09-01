using AudiobookConverter.FFmpeg.Execution;

namespace AudiobookConverter.FFmpeg.Tests.Execution;

public sealed class ConversionProgressParserTests
{
  [Fact]
  public void ParseRetainsRawOutTimeAndStageIndex()
  {
    var progress = ConversionProgressParser.Parse("out_time_us=1250000", 3);
    Assert.Equal(1_250_000, progress!.OutTimeMicroseconds);
    Assert.Equal(3, progress.StageIndex);
  }

  [Theory]
  [InlineData("speed=2.5x", 2.5)]
  [InlineData("speed=N/A", null)]
  public void ParseHandlesSpeedValues(string line, double? expected)
  {
    var progress = ConversionProgressParser.Parse(line, 0);
    Assert.Equal(expected, progress?.Speed);
  }

  [Fact]
  public void ParseIgnoresNonProgressOutput() => Assert.Null(ConversionProgressParser.Parse("ffmpeg version", 0));
}
