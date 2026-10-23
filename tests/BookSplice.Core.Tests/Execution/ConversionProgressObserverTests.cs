using BookSplice.Core.Execution;

namespace BookSplice.Core.Tests.Execution;

public sealed class ConversionProgressObserverTests
{
  [Fact]
  public void SnapshotTicksDoNotPostponeSilenceAdvisory()
  {
    var observer = new ConversionProgressObserver();
    observer.Report(Entry(ConversionPhase.MetadataWriting), TimeSpan.Zero);

    for (var second = 1; second < 30; second++)
      Assert.Equal(ProgressAdvisory.None, observer.Snapshot(TimeSpan.FromSeconds(second)).Advisory);

    Assert.Equal(ProgressAdvisory.None, observer.Snapshot(TimeSpan.FromSeconds(29.999)).Advisory);
    var overdue = observer.Snapshot(TimeSpan.FromSeconds(30));
    Assert.Equal(ProgressAdvisory.NoUpdate, overdue.Advisory);
    Assert.Equal(TimeSpan.FromSeconds(30), overdue.LastReportAge);
    Assert.Null(overdue.Percent);
    Assert.Null(overdue.Remaining);
  }

  [Fact]
  public void RepeatedMeasuredReportsBecomeNoAdvanceAtThirtySeconds()
  {
    var observer = new ConversionProgressObserver();
    var occurrence = Guid.NewGuid();
    observer.Report(Entry(ConversionPhase.Encoding, occurrence, measurable: true) with { PhasePercent = 20 }, TimeSpan.Zero);

    for (var second = 1; second <= 30; second++)
      observer.Report(Measure(ConversionPhase.Encoding, occurrence, 20), TimeSpan.FromSeconds(second));

    var snapshot = observer.Snapshot(TimeSpan.FromSeconds(30));
    Assert.Equal(ProgressAdvisory.NoAdvance, snapshot.Advisory);
    Assert.Equal(TimeSpan.Zero, snapshot.LastReportAge);
    Assert.Equal(TimeSpan.FromSeconds(30), snapshot.LastAdvanceAge);
    Assert.Equal(20, snapshot.Percent);
    Assert.Null(snapshot.Remaining);
  }

  [Fact]
  public void UnmeasurableReportsNeverSuggestNoAdvance()
  {
    var observer = new ConversionProgressObserver();
    var occurrence = Guid.NewGuid();
    observer.Report(Entry(ConversionPhase.ValidationChecks, occurrence), TimeSpan.Zero);
    observer.Report(Measure(ConversionPhase.ValidationChecks, occurrence, null), TimeSpan.FromSeconds(30));

    Assert.Equal(ProgressAdvisory.None, observer.Snapshot(TimeSpan.FromSeconds(30)).Advisory);
    Assert.Equal(ProgressAdvisory.NoUpdate, observer.Snapshot(TimeSpan.FromSeconds(60)).Advisory);
  }

  [Fact]
  public void ExpectedMeasurementsWithNoValidNumberBecomeNoAdvance()
  {
    var observer = new ConversionProgressObserver();
    var occurrence = Guid.NewGuid();
    observer.Report(Entry(ConversionPhase.Encoding, occurrence, measurable: true), TimeSpan.Zero);
    for (var second = 1; second <= 30; second++)
      observer.Report(Measure(ConversionPhase.Encoding, occurrence, null), TimeSpan.FromSeconds(second));

    var snapshot = observer.Snapshot(TimeSpan.FromSeconds(30));
    Assert.Equal(ProgressAdvisory.NoAdvance, snapshot.Advisory);
    Assert.Null(snapshot.Percent);
    Assert.Equal(TimeSpan.FromSeconds(30), snapshot.LastAdvanceAge);
  }

  [Fact]
  public void EtaExpiresAfterTenSecondsWithoutNumericAdvance()
  {
    var observer = new ConversionProgressObserver();
    var occurrence = Guid.NewGuid();
    observer.Report(Entry(ConversionPhase.Encoding, occurrence, measurable: true) with { PhasePercent = 0 }, TimeSpan.Zero);
    observer.Report(Measure(ConversionPhase.Encoding, occurrence, 10, 2), TimeSpan.FromSeconds(3));

    var fresh = observer.Snapshot(TimeSpan.FromSeconds(13));
    Assert.Equal(TimeSpan.FromSeconds(17), fresh.Remaining);
    Assert.Equal(2, fresh.Speed);
    var stale = observer.Snapshot(TimeSpan.FromSeconds(13.001));
    Assert.Null(stale.Remaining);
    Assert.Null(stale.Speed);
    Assert.Equal(10, stale.Percent);
  }

  [Fact]
  public void EtaCountsDownBetweenReportsAndDisappearsAtProjectedCompletion()
  {
    var observer = new ConversionProgressObserver();
    var occurrence = Guid.NewGuid();
    observer.Report(Entry(ConversionPhase.Encoding, occurrence, measurable: true) with { PhasePercent = 0 }, TimeSpan.Zero);
    observer.Report(Measure(ConversionPhase.Encoding, occurrence, 80, 2), TimeSpan.FromSeconds(3));

    Assert.Equal(TimeSpan.FromSeconds(0.75), observer.Snapshot(TimeSpan.FromSeconds(3)).Remaining);
    Assert.Null(observer.Snapshot(TimeSpan.FromSeconds(3.75)).Remaining);
  }

  [Fact]
  public void FreshAdvancesAfterStalenessBuildNewEtaSamples()
  {
    var observer = new ConversionProgressObserver();
    var occurrence = Guid.NewGuid();
    observer.Report(Entry(ConversionPhase.Encoding, occurrence, measurable: true) with { PhasePercent = 0 }, TimeSpan.Zero);
    observer.Report(Measure(ConversionPhase.Encoding, occurrence, 30, 2), TimeSpan.FromSeconds(3));
    Assert.Null(observer.Snapshot(TimeSpan.FromSeconds(13.001)).Remaining);

    observer.Report(Measure(ConversionPhase.Encoding, occurrence, 40, 2), TimeSpan.FromSeconds(14));
    Assert.Null(observer.Snapshot(TimeSpan.FromSeconds(14)).Remaining);
    observer.Report(Measure(ConversionPhase.Encoding, occurrence, 50, 2), TimeSpan.FromSeconds(17));
    Assert.Equal(TimeSpan.FromSeconds(15), observer.Snapshot(TimeSpan.FromSeconds(17)).Remaining);
  }

  [Fact]
  public void PhaseEntryClearsMeasurementAndAdvisory()
  {
    var observer = new ConversionProgressObserver();
    var encoding = Guid.NewGuid();
    observer.Report(Entry(ConversionPhase.Encoding, encoding, measurable: true) with { PhasePercent = 50, PhaseSpeed = 2 }, TimeSpan.Zero);
    Assert.Equal(ProgressAdvisory.NoUpdate, observer.Snapshot(TimeSpan.FromSeconds(30)).Advisory);

    var metadata = Guid.NewGuid();
    observer.Report(Entry(ConversionPhase.MetadataWriting, metadata), TimeSpan.FromSeconds(31));
    var snapshot = observer.Snapshot(TimeSpan.FromSeconds(31));
    Assert.Equal(ConversionPhase.MetadataWriting, snapshot.Phase);
    Assert.Equal(metadata, snapshot.PhaseOccurrenceId);
    Assert.Null(snapshot.Percent);
    Assert.Null(snapshot.Speed);
    Assert.Null(snapshot.Remaining);
    Assert.Equal(ProgressAdvisory.None, snapshot.Advisory);
    Assert.Equal(TimeSpan.Zero, snapshot.LastReportAge);
    Assert.Equal(TimeSpan.Zero, snapshot.LastAdvanceAge);
  }

  [Theory]
  [InlineData(double.NaN)]
  [InlineData(double.PositiveInfinity)]
  [InlineData(double.NegativeInfinity)]
  [InlineData(-1)]
  public void MalformedMeasurementsDoNotRefreshAdvanceClock(double invalid)
  {
    var observer = new ConversionProgressObserver();
    var occurrence = Guid.NewGuid();
    observer.Report(Entry(ConversionPhase.Encoding, occurrence, measurable: true) with { PhasePercent = 25 }, TimeSpan.Zero);
    observer.Report(Measure(ConversionPhase.Encoding, occurrence, invalid, invalid), TimeSpan.FromSeconds(30));

    var snapshot = observer.Snapshot(TimeSpan.FromSeconds(30));
    Assert.Equal(25, snapshot.Percent);
    Assert.Null(snapshot.Speed);
    Assert.Equal(TimeSpan.FromSeconds(30), snapshot.LastAdvanceAge);
    Assert.Equal(ProgressAdvisory.NoAdvance, snapshot.Advisory);
  }

  [Fact]
  public void FinitePercentageIsClampedToPhaseRange()
  {
    var observer = new ConversionProgressObserver();
    var occurrence = Guid.NewGuid();
    observer.Report(Entry(ConversionPhase.Encoding, occurrence, measurable: true) with { PhasePercent = 120 }, TimeSpan.Zero);

    Assert.Equal(100, observer.Snapshot(TimeSpan.Zero).Percent);
  }

  [Fact]
  public void WrongJobOldSequenceAndRetiredOccurrenceCannotReplaceCurrentPhase()
  {
    var observer = new ConversionProgressObserver();
    var job = Guid.NewGuid();
    var encoding = Guid.NewGuid();
    var metadata = Guid.NewGuid();
    Assert.True(observer.Report(Entry(ConversionPhase.Encoding, encoding, measurable: true) with { JobId = job, Sequence = 1 }, TimeSpan.Zero));
    Assert.True(observer.Report(Entry(ConversionPhase.MetadataWriting, metadata) with { JobId = job, Sequence = 3 }, TimeSpan.FromSeconds(1)));
    Assert.False(observer.Report(Measure(ConversionPhase.Encoding, encoding, 90) with { JobId = job, Sequence = 4 }, TimeSpan.FromSeconds(2)));
    Assert.False(observer.Report(Measure(ConversionPhase.MetadataWriting, metadata, 90) with { JobId = job, Sequence = 3 }, TimeSpan.FromSeconds(3)));
    Assert.False(observer.Report(Measure(ConversionPhase.MetadataWriting, metadata, 90) with { JobId = Guid.NewGuid(), Sequence = 5 }, TimeSpan.FromSeconds(4)));

    var snapshot = observer.Snapshot(TimeSpan.FromSeconds(4));
    Assert.Equal(ConversionPhase.MetadataWriting, snapshot.Phase);
    Assert.Null(snapshot.Percent);
    Assert.Equal(job, snapshot.JobId);
    Assert.Equal(3, snapshot.Sequence);
    Assert.Equal(TimeSpan.FromSeconds(3), snapshot.LastReportAge);
  }

  [Fact]
  public void LegacyProgressIsAcceptedWithoutCreatingWholeJobCompletion()
  {
    var observer = new ConversionProgressObserver();

    Assert.True(observer.Report(new ConversionProgress(null, null, "completed", 0, DisplayPercent: 100), TimeSpan.Zero));
    var snapshot = observer.Snapshot(TimeSpan.Zero);
    Assert.Null(snapshot.Phase);
    Assert.Null(snapshot.Percent);
    Assert.Null(snapshot.Remaining);
  }

  [Fact]
  public void AcceptedPhaseCountsAndEnvelopeAreRetained()
  {
    var observer = new ConversionProgressObserver();
    var job = Guid.NewGuid();
    var occurrence = Guid.NewGuid();
    observer.Report(Entry(ConversionPhase.Encoding, occurrence, measurable: true) with
    {
      JobId = job,
      Sequence = 1,
      CompletedSources = 2,
      TotalSources = 5
    }, TimeSpan.Zero);

    var snapshot = observer.Snapshot(TimeSpan.Zero);
    Assert.Equal(2, snapshot.CompletedSources);
    Assert.Equal(5, snapshot.TotalSources);
    Assert.Equal(job, snapshot.JobId);
    Assert.Equal(1, snapshot.Sequence);
  }

  private static ConversionProgress Entry(ConversionPhase phase, Guid? occurrence = null, bool measurable = false) =>
    new(null, null, null, 0, Phase: phase, PhaseOccurrenceId: occurrence ?? Guid.NewGuid(),
      IsPhaseEntry: true, ExpectsMeasurableUpdates: measurable);

  private static ConversionProgress Measure(ConversionPhase phase, Guid occurrence, double? percent, double? speed = null) =>
    new(null, null, null, 0, Phase: phase, PhaseOccurrenceId: occurrence, PhasePercent: percent, PhaseSpeed: speed);
}
