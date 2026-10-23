using BookSplice.Core.Execution;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class ConversionProgressDisplayTests
{
  [Fact]
  public void BeforeFirstPhaseOnlyStartingConversionIsClaimed()
  {
    var display = new ConversionProgressDisplay();
    Assert.Equal("Starting conversion", display.PhaseLabel);
    Assert.Contains("Starting conversion", display.Describe(TimeSpan.Zero));
    Assert.DoesNotContain("Preparing conversion", display.Describe(TimeSpan.Zero));
  }

  [Theory]
  [InlineData(47.3609504, "Elapsed: 47s")]
  [InlineData(1290.2067871, "Elapsed: 21m 30s")]
  [InlineData(3630, "Elapsed: 1h 0m 30s")]
  [InlineData(93661, "Elapsed: 26h 1m 1s")]
  public void ElapsedTextUsesWholeSecondsAndDoesNotWrapAfterADay(double seconds, string expected)
    => Assert.Equal(expected, ConversionProgressDisplay.ElapsedText(TimeSpan.FromSeconds(seconds)));

  [Theory]
  [InlineData(ConversionPhase.Analysis, "Reanalyzing sources")]
  [InlineData(ConversionPhase.Planning, "Planning conversion")]
  [InlineData(ConversionPhase.Preparation, "Preparing conversion")]
  [InlineData(ConversionPhase.Encoding, "Encoding audio")]
  [InlineData(ConversionPhase.StreamCopy, "Copying audio")]
  [InlineData(ConversionPhase.Assembly, "Assembling audio")]
  [InlineData(ConversionPhase.MetadataWriting, "Writing metadata")]
  [InlineData(ConversionPhase.ValidationChecks, "Checking output")]
  [InlineData(ConversionPhase.FullDecode, "Validating full audio")]
  [InlineData(ConversionPhase.Publication, "Publishing output")]
  [InlineData(ConversionPhase.Cleanup, "Cleaning up")]
  [InlineData(ConversionPhase.AuditWriting, "Writing audit")]
  public void NamedPhaseIsShownWithUnavailableNumericProgress(ConversionPhase phase, string label)
  {
    var display = new ConversionProgressDisplay();
    display.Report(Entry(phase), TimeSpan.Zero);

    Assert.Equal(label, display.PhaseLabel);
    Assert.Contains("Numeric progress unavailable", display.Describe(TimeSpan.Zero));
    Assert.Null(display.Snapshot(TimeSpan.Zero).Percent);
  }

  [Fact]
  public void NewPhaseClearsPreviousPercentageSpeedAndEta()
  {
    var display = new ConversionProgressDisplay();
    var encoding = Guid.NewGuid();
    display.Report(Entry(ConversionPhase.Encoding, encoding) with { PhasePercent = 0 }, TimeSpan.Zero);
    display.Report(Measure(ConversionPhase.Encoding, encoding, 30, 2), TimeSpan.FromSeconds(3));
    Assert.Equal(30, display.Snapshot(TimeSpan.FromSeconds(3)).Percent);
    Assert.NotNull(display.Snapshot(TimeSpan.FromSeconds(3)).Remaining);

    display.Report(Entry(ConversionPhase.ValidationChecks), TimeSpan.FromSeconds(4));
    Assert.Equal("Checking output", display.PhaseLabel);
    Assert.Null(display.Snapshot(TimeSpan.FromSeconds(4)).Percent);
    Assert.Null(display.Snapshot(TimeSpan.FromSeconds(4)).Speed);
    Assert.Null(display.Snapshot(TimeSpan.FromSeconds(4)).Remaining);
    Assert.Contains("Numeric progress unavailable", display.Describe(TimeSpan.FromSeconds(4)));
  }

  [Fact]
  public void PhaseRemainingCountsDownThenExpiresAfterTenSeconds()
  {
    var display = new ConversionProgressDisplay();
    var occurrence = Guid.NewGuid();
    display.Report(Entry(ConversionPhase.Encoding, occurrence) with { PhasePercent = 0 }, TimeSpan.Zero);
    display.Report(Measure(ConversionPhase.Encoding, occurrence, 10, 2), TimeSpan.FromSeconds(3));

    Assert.Contains("Phase remaining: 27s", display.Describe(TimeSpan.FromSeconds(3)));
    Assert.Contains("Phase remaining: 17s", display.Describe(TimeSpan.FromSeconds(13)));
    Assert.DoesNotContain("Phase remaining: 17s", display.Describe(TimeSpan.FromSeconds(13.001)));
    Assert.Contains("10%", display.Describe(TimeSpan.FromSeconds(13.001)));
  }

  [Fact]
  public void SilenceAndNoAdvanceUseExactAdvisoryWordingAtThirtySeconds()
  {
    var silence = new ConversionProgressDisplay();
    silence.Report(Entry(ConversionPhase.MetadataWriting), TimeSpan.Zero);
    Assert.DoesNotContain("No update received", silence.Describe(TimeSpan.FromSeconds(29.999)));
    Assert.Contains("No update received for 30s; operation has not returned", silence.Describe(TimeSpan.FromSeconds(30)));

    var noAdvance = new ConversionProgressDisplay();
    var occurrence = Guid.NewGuid();
    noAdvance.Report(Entry(ConversionPhase.Encoding, occurrence), TimeSpan.Zero);
    for (var second = 1; second <= 30; second++)
      noAdvance.Report(Measure(ConversionPhase.Encoding, occurrence, null), TimeSpan.FromSeconds(second));
    Assert.Contains("Updates received, but progress has not advanced for 30s", noAdvance.Describe(TimeSpan.FromSeconds(30)));
  }

  [Fact]
  public void ExecutorCompletionNeverClaimsConversionIsComplete()
  {
    var display = new ConversionProgressDisplay();
    display.Report(Entry(ConversionPhase.MetadataWriting) with { State = "completed", DisplayPercent = 100 }, TimeSpan.Zero);

    Assert.Equal("Writing metadata", display.PhaseLabel);
    Assert.DoesNotContain("Complete", display.Describe(TimeSpan.Zero), StringComparison.OrdinalIgnoreCase);
    Assert.Null(display.Snapshot(TimeSpan.Zero).Percent);
  }

  private static ConversionProgress Entry(ConversionPhase phase, Guid? occurrence = null) =>
    new(null, null, null, 0, Phase: phase, PhaseOccurrenceId: occurrence ?? Guid.NewGuid(), IsPhaseEntry: true,
      ExpectsMeasurableUpdates: phase is ConversionPhase.Encoding or ConversionPhase.StreamCopy or ConversionPhase.Assembly or ConversionPhase.FullDecode);

  private static ConversionProgress Measure(ConversionPhase phase, Guid occurrence, double? percent, double? speed = null) =>
    new(null, null, null, 0, Phase: phase, PhaseOccurrenceId: occurrence, PhasePercent: percent, PhaseSpeed: speed);
}
