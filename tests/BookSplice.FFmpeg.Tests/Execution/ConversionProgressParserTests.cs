using BookSplice.FFmpeg.Execution;

namespace BookSplice.FFmpeg.Tests.Execution;

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

  [Theory]
  [InlineData("out_time_us=not-a-number")]
  [InlineData("out_time_us=")]
  [InlineData("speed=still-bad")]
  public void ParseIgnoresMalformedTimeButRetainsFieldIdentity(string line)
  {
    var progress = ConversionProgressParser.Parse(line, 4, 2);
    if (line.StartsWith("speed", StringComparison.Ordinal)) Assert.NotNull(progress);
    else Assert.Null(progress);
  }

  [Fact]
  public void ParseRetainsStageAndSourceForProgressState()
  {
    var progress = ConversionProgressParser.Parse("progress=continue", 9, 5);
    Assert.Equal("continue", progress!.State);
    Assert.Equal(9, progress.StageIndex);
    Assert.Equal(5, progress.SourceIndex);
  }
}
