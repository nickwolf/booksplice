using BookSplice.Core.Execution;
using BookSplice.Core.Planning;
using BookSplice.FFmpeg.Execution;

namespace BookSplice.FFmpeg.Tests.Execution;

public sealed class ConversionPhaseProgressTests
{
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void ParallelSegmentsUseDurationWeightedBookProgress(bool reverseOrder)
  {
    var events = new List<ConversionProgress>();
    var clock = new ManualClock();
    var plan = FFmpegConversionExecutorTests.Plan(AudioStrategy.SegmentedTranscode, 2, 2, durations: [10_000_000, 30_000_000]);
    var normalizer = new ConversionProgressNormalizer(plan, new InlineProgress(events.Add), clock);
    using var encoding = normalizer.BeginPhase(ConversionPhase.Encoding);
    var first = normalizer.ForProcess(0, 0);
    var second = normalizer.ForProcess(1, 1);
    var reports = new Action[] { () => first.Report("out_time_us=5000000"), () => second.Report("out_time_us=15000000") };
    if (reverseOrder) Array.Reverse(reports);
    foreach (var report in reports) report();

    Assert.Equal(50, events[^1].PhasePercent);
    Assert.Equal(ConversionPhase.Encoding, events[^1].Phase);
    Assert.True(events[^1].ExpectsMeasurableUpdates);
    Assert.Equal(events[0].PhaseOccurrenceId, events[^1].PhaseOccurrenceId);
    Assert.DoesNotContain(events, item => item.State == "completed");
  }

  [Fact]
  public void UnknownDurationsRemainIndeterminateButCompletedCountIsKnown()
  {
    var events = new List<ConversionProgress>();
    var plan = FFmpegConversionExecutorTests.Plan(AudioStrategy.SegmentedTranscode, 33, 3, durations: Enumerable.Repeat<long?>(null, 33).ToArray());
    var normalizer = new ConversionProgressNormalizer(plan, new InlineProgress(events.Add));
    using var phase = normalizer.BeginPhase(ConversionPhase.Encoding);
    normalizer.ForProcess(0, 0).Report("out_time_us=5000000");
    normalizer.CompleteProcess(0, 0);
    normalizer.CompleteProcess(0, 0);

    Assert.All(events, item => Assert.Null(item.PhasePercent));
    Assert.All(events, item => Assert.False(item.ExpectsMeasurableUpdates));
    Assert.Equal(1, events[^1].CompletedSources);
    Assert.Equal(33, events[^1].TotalSources);
  }

  [Fact]
  public void RetiredProcessCannotUpdateAssemblyOrLiveSibling()
  {
    var events = new List<ConversionProgress>();
    var plan = FFmpegConversionExecutorTests.Plan(AudioStrategy.SegmentedTranscode, 2, 2, durations: [10_000_000, 30_000_000]);
    var normalizer = new ConversionProgressNormalizer(plan, new InlineProgress(events.Add));
    IProgress<string>? retired = null;
    using (normalizer.BeginPhase(ConversionPhase.Encoding))
    {
      retired = normalizer.ForProcess(0, 0);
      var live = normalizer.ForProcess(1, 1);
      retired.Report("out_time_us=5000000");
      normalizer.CompleteProcess(0, 0);
      var beforeLate = events.Count;
      retired!.Report("out_time_us=10000000");
      Assert.Equal(beforeLate, events.Count);
      live.Report("out_time_us=15000000");
    }
    using (normalizer.BeginPhase(ConversionPhase.Assembly))
    {
      var beforeLate = events.Count;
      retired.Report("out_time_us=10000000");
      Assert.Equal(beforeLate, events.Count);
    }
    Assert.Equal(ConversionPhase.Assembly, events[^1].Phase);
  }

  [Fact]
  public void BackwardsAndExcessiveTimesStayFiniteAndSpeedUsesAggregateWallTime()
  {
    var events = new List<ConversionProgress>();
    var clock = new ManualClock();
    var plan = FFmpegConversionExecutorTests.Plan(AudioStrategy.SegmentedTranscode, 2, 2, durations: [10_000_000, 30_000_000]);
    var normalizer = new ConversionProgressNormalizer(plan, new InlineProgress(events.Add), clock);
    using var phase = normalizer.BeginPhase(ConversionPhase.Encoding);
    var first = normalizer.ForProcess(0, 0);
    var second = normalizer.ForProcess(1, 1);
    first.Report("speed=999x");
    first.Report("out_time_us=5000000");
    second.Report("speed=0.01x");
    clock.Advance(TimeSpan.FromSeconds(10));
    second.Report("out_time_us=15000000");
    Assert.Equal(2, events[^1].PhaseSpeed);
    first.Report("out_time_us=1000000");
    Assert.Equal(50, events[^1].PhasePercent);
    first.Report("out_time_us=9223372036854775807");
    Assert.Equal(62.5, events[^1].PhasePercent);
    Assert.All(events.Where(item => item.PhasePercent.HasValue), item => Assert.True(double.IsFinite(item.PhasePercent!.Value)));
    Assert.All(events.Where(item => item.PhaseSpeed.HasValue), item => Assert.True(double.IsFinite(item.PhaseSpeed!.Value)));
  }

  private sealed class InlineProgress(Action<ConversionProgress> action) : IProgress<ConversionProgress>
  {
    public void Report(ConversionProgress value) => action(value);
  }

  private sealed class ManualClock : TimeProvider
  {
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
  }
}
