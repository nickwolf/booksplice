using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class ConversionProgressDisplayTests
{
  [Theory]
  [InlineData(47.3609504, "Elapsed: 47s")]
  [InlineData(1290.2067871, "Elapsed: 21m 30s")]
  [InlineData(3630, "Elapsed: 1h 0m 30s")]
  [InlineData(93661, "Elapsed: 26h 1m 1s")]
  public void ElapsedTextUsesWholeSecondsAndDoesNotWrapAfterADay(double seconds, string expected)
    => Assert.Equal(expected, ConversionProgressDisplay.ElapsedText(TimeSpan.FromSeconds(seconds)));

  [Fact]
  public void RemainingTimeUsesOverallProgressAndCountsDownBetweenUpdates()
  {
    var display = new ConversionProgressDisplay();
    display.Report(new(null, 12.3, null, 0, null, 24.75), TimeSpan.FromSeconds(10));
    Assert.Equal("Elapsed: 10s · Speed: 12.3× · Est. remaining: 30s", display.Describe(TimeSpan.FromSeconds(10)));
    Assert.Equal("Elapsed: 12s · Speed: 12.3× · Est. remaining: 28s", display.Describe(TimeSpan.FromSeconds(12)));

    display.Report(new(null, null, "continue", 0, null, 49.5), TimeSpan.FromSeconds(20));
    Assert.Equal("Elapsed: 20s · Speed: 12.3× · Est. remaining: 20s", display.Describe(TimeSpan.FromSeconds(20)));
  }

  [Fact]
  public void EstimateAdaptsToRecentSlowerProgressInsteadOfKeepingStartupSpeed()
  {
    var display = new ConversionProgressDisplay();
    display.Report(new(null, 20, null, 0, null, 9.9), TimeSpan.FromSeconds(10));
    display.Report(new(null, 20, null, 0, null, 19.8), TimeSpan.FromSeconds(20));
    display.Report(new(null, 10, null, 0, null, 29.7), TimeSpan.FromSeconds(40));
    Assert.Equal("Elapsed: 40s · Speed: 10.0× · Est. remaining: 1m 45s", display.Describe(TimeSpan.FromSeconds(40)));
  }

  [Fact]
  public void StalledProgressDoesNotKeepCountingDownToZero()
  {
    var display = new ConversionProgressDisplay();
    display.Report(new(null, 10, null, 0, null, 49.5), TimeSpan.FromSeconds(10));
    Assert.EndsWith("Est. remaining: Calculating…", display.Describe(TimeSpan.FromSeconds(21)), StringComparison.Ordinal);
  }

  [Fact]
  public void StageChangeDoesNotReuseThePreviousProcessesSpeed()
  {
    var display = new ConversionProgressDisplay();
    display.Report(new(null, 12.3, null, 0), TimeSpan.FromSeconds(2));
    display.Report(new(null, null, "continue", 1), TimeSpan.FromSeconds(3));
    Assert.Contains("Speed: Calculating…", display.Describe(TimeSpan.FromSeconds(3)), StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(double.NaN)]
  [InlineData(double.PositiveInfinity)]
  [InlineData(-1)]
  public void InvalidSpeedNeverAppearsAsANumber(double speed)
  {
    var display = new ConversionProgressDisplay();
    display.Report(new(null, speed, null, 0), TimeSpan.FromSeconds(1));
    Assert.Equal("Elapsed: 1s · Speed: Calculating… · Est. remaining: Calculating…", display.Describe(TimeSpan.FromSeconds(1)));
  }

  [Fact]
  public void ACompletedSegmentDoesNotClaimTheWholeBookIsFinalizing()
  {
    var display = new ConversionProgressDisplay();
    display.Report(new(null, null, "end", 0, 0, 24.75), TimeSpan.FromSeconds(10));
    Assert.Contains("Est. remaining: 30s", display.Describe(TimeSpan.FromSeconds(10)), StringComparison.Ordinal);
  }

  [Fact]
  public void EncodingCompletionShowsFinalizingUntilTheServiceReturns()
  {
    var display = new ConversionProgressDisplay();
    display.Report(new(null, 12.3, "completed", int.MaxValue, null, 100), TimeSpan.FromSeconds(60));
    Assert.Equal("Elapsed: 1m 0s · Finalizing…", display.Describe(TimeSpan.FromSeconds(60)));
  }
}
