using System.Text.Json;
using BookSplice.Cli;
using BookSplice.Core.Execution;

namespace BookSplice.Cli.Tests;

public sealed class CliProgressDisplayTests
{
  private static readonly JsonSerializerOptions LegacyJsonOptions = new(JsonSerializerDefaults.Web);

  [Fact]
  public async Task JsonProgressRetainsExecutionFieldsAndAddsPhaseIdentity()
  {
    var output = new StringWriter();
    await using var display = new CliProgressDisplay(output, true);
    display.StartAttempt();
    var job = Guid.NewGuid();
    var occurrence = Guid.NewGuid();

    display.Report(new(1_000_000, 1.5, "continue", 2, 1, 25,
      ConversionPhase.Encoding, occurrence, true, 40, 1.25, true, 1, 3, job, 4));
    await display.StopAttemptAsync();

    using var document = JsonDocument.Parse(Assert.Single(Lines(output)));
    var progress = document.RootElement;
    Assert.Equal(1, progress.GetProperty("schemaVersion").GetInt32());
    Assert.Equal("progress", progress.GetProperty("event").GetString());
    Assert.Equal(25, progress.GetProperty("displayPercent").GetDouble());
    Assert.Equal(1.5, progress.GetProperty("speed").GetDouble());
    Assert.Equal(2, progress.GetProperty("stageIndex").GetInt32());
    Assert.Equal(1, progress.GetProperty("sourceIndex").GetInt32());
    Assert.Equal("continue", progress.GetProperty("state").GetString());
    Assert.Equal("Encoding", progress.GetProperty("phase").GetString());
    Assert.Equal(40, progress.GetProperty("phasePercent").GetDouble());
    Assert.Equal(1.25, progress.GetProperty("phaseSpeed").GetDouble());
    Assert.Equal("phase", progress.GetProperty("measurementScope").GetString());
    Assert.Equal(job, progress.GetProperty("jobId").GetGuid());
    Assert.Equal(4, progress.GetProperty("sequence").GetInt64());
    Assert.Equal(occurrence, progress.GetProperty("phaseOccurrenceId").GetGuid());
    Assert.True(progress.GetProperty("expectsMeasurableUpdates").GetBoolean());
    Assert.Equal(1, progress.GetProperty("completedSources").GetInt32());
    Assert.Equal(3, progress.GetProperty("totalSources").GetInt32());
    var oldReader = JsonSerializer.Deserialize<LegacyProgress>(progress.GetRawText(), LegacyJsonOptions);
    Assert.Equal(25, oldReader?.DisplayPercent);
    Assert.Equal(1.5, oldReader?.Speed);
    Assert.Equal(2, oldReader?.StageIndex);
    Assert.Equal(1, oldReader?.SourceIndex);
    Assert.Equal("continue", oldReader?.State);
  }

  [Fact]
  public async Task ServicePhaseHasNullLegacyMeasurements()
  {
    var output = new StringWriter();
    await using var display = new CliProgressDisplay(output, true);
    display.StartAttempt();
    display.Report(new(null, null, null, 0, Phase: ConversionPhase.Analysis,
      PhaseOccurrenceId: Guid.NewGuid(), IsPhaseEntry: true, JobId: Guid.NewGuid(), Sequence: 1));
    await display.StopAttemptAsync();

    using var document = JsonDocument.Parse(Assert.Single(Lines(output)));
    var progress = document.RootElement;
    Assert.Equal(JsonValueKind.Null, progress.GetProperty("displayPercent").ValueKind);
    Assert.Equal(JsonValueKind.Null, progress.GetProperty("speed").ValueKind);
    Assert.Equal(JsonValueKind.Null, progress.GetProperty("sourceIndex").ValueKind);
    Assert.Equal(JsonValueKind.Null, progress.GetProperty("state").ValueKind);
    Assert.Equal("Analysis", progress.GetProperty("phase").GetString());
  }

  [Fact]
  public async Task HumanOutputNamesPhaseAndLimitsRemainingToMeasuredPhase()
  {
    var output = new StringWriter();
    await using var display = new CliProgressDisplay(output, false);
    display.StartAttempt();
    var analysis = Guid.NewGuid();
    var encoding = Guid.NewGuid();
    display.Report(new(null, null, null, 0, Phase: ConversionPhase.Analysis, PhaseOccurrenceId: analysis, IsPhaseEntry: true));
    display.Report(new(null, null, null, 0, Phase: ConversionPhase.Encoding, PhaseOccurrenceId: encoding, IsPhaseEntry: true, ExpectsMeasurableUpdates: true));
    display.Report(new(null, null, null, 0, Phase: ConversionPhase.Encoding, PhaseOccurrenceId: encoding, PhasePercent: 20));
    await display.StopAttemptAsync();

    Assert.Contains("Analyzing sources", output.ToString());
    Assert.Contains("Numeric progress unavailable", output.ToString());
    Assert.Contains("Encoding audio", output.ToString());
    Assert.Contains("Phase remaining", output.ToString());
    Assert.DoesNotContain("completed validation", output.ToString(), StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task SilentAttemptEmitsAdvisoryWithoutHeartbeat()
  {
    var clock = new ManualTimeProvider();
    var output = new StringWriter();
    await using var display = new CliProgressDisplay(output, true, clock);
    display.StartAttempt();

    clock.Advance(TimeSpan.FromSeconds(30));
    await UntilAsync(() => Lines(output).Length == 1);
    using var advisory = JsonDocument.Parse(Assert.Single(Lines(output)));
    Assert.Equal("noUpdate", advisory.RootElement.GetProperty("advisory").GetString());
    Assert.Equal(30, advisory.RootElement.GetProperty("lastReportAgeSeconds").GetDouble());
    Assert.Equal(30, advisory.RootElement.GetProperty("observationElapsedSeconds").GetDouble());
    Assert.Equal(JsonValueKind.Null, advisory.RootElement.GetProperty("sequence").ValueKind);

    clock.Advance(TimeSpan.FromSeconds(1));
    await UntilAsync(() => Lines(output).Length == 2);
    Assert.Equal(2, Lines(output).Length);
    await display.StopAttemptAsync();
    clock.Advance(TimeSpan.FromSeconds(30));
    Assert.Equal(2, Lines(output).Length);
  }

  [Fact]
  public async Task RepeatedMeasurementsAdvisoryKeepsOperationSequenceAndRecoveryUsesFreshReport()
  {
    var clock = new ManualTimeProvider();
    var output = new StringWriter();
    await using var display = new CliProgressDisplay(output, true, clock);
    display.StartAttempt();
    var job = Guid.NewGuid();
    var occurrence = Guid.NewGuid();
    display.Report(new(null, null, null, 0, Phase: ConversionPhase.Encoding, PhaseOccurrenceId: occurrence,
      IsPhaseEntry: true, ExpectsMeasurableUpdates: true, JobId: job, Sequence: 1));
    clock.Advance(TimeSpan.FromSeconds(29));
    display.Report(new(null, null, "continue", 0, Phase: ConversionPhase.Encoding, PhaseOccurrenceId: occurrence,
      PhasePercent: 0, JobId: job, Sequence: 2));
    clock.Advance(TimeSpan.FromSeconds(1));
    await UntilAsync(() => Lines(output).Length == 3);
    using (var advisory = JsonDocument.Parse(Lines(output)[2]))
    {
      Assert.Equal("noAdvance", advisory.RootElement.GetProperty("advisory").GetString());
      Assert.Equal(2, advisory.RootElement.GetProperty("sequence").GetInt64());
      Assert.Equal(1, advisory.RootElement.GetProperty("lastReportAgeSeconds").GetDouble());
      Assert.Equal(30, advisory.RootElement.GetProperty("lastAdvanceAgeSeconds").GetDouble());
      Assert.True(advisory.RootElement.GetProperty("expectsMeasurableUpdates").GetBoolean());
    }
    display.Report(new(null, null, "continue", 0, Phase: ConversionPhase.Encoding, PhaseOccurrenceId: occurrence,
      PhasePercent: 1, JobId: job, Sequence: 3));
    using var recovery = JsonDocument.Parse(Lines(output)[3]);
    Assert.Equal("none", recovery.RootElement.GetProperty("advisory").GetString());
    Assert.Equal(3, recovery.RootElement.GetProperty("sequence").GetInt64());
    Assert.True(recovery.RootElement.GetProperty("expectsMeasurableUpdates").GetBoolean());
    await display.StopAttemptAsync();
  }

  [Fact]
  public async Task HumanLegacyExecutionStillShowsProgress()
  {
    var output = new StringWriter();
    await using var display = new CliProgressDisplay(output, false);
    display.StartAttempt();
    display.Report(new(null, 2, "continue", 0, DisplayPercent: 50));
    await display.StopAttemptAsync();
    Assert.Contains("Progress 50%", output.ToString());
  }

  [Fact]
  public async Task InvalidPhaseMeasurementsSerializeAsNull()
  {
    var output = new StringWriter();
    await using var display = new CliProgressDisplay(output, true);
    display.StartAttempt();
    var occurrence = Guid.NewGuid();
    display.Report(new(null, null, null, 0, Phase: ConversionPhase.Encoding,
      PhaseOccurrenceId: occurrence, IsPhaseEntry: true));
    display.Report(new(null, null, "continue", 0, Phase: ConversionPhase.Encoding,
      PhaseOccurrenceId: occurrence, PhasePercent: double.NaN, PhaseSpeed: double.PositiveInfinity));
    await display.StopAttemptAsync();

    using var line = JsonDocument.Parse(Lines(output)[1]);
    Assert.Equal(JsonValueKind.Null, line.RootElement.GetProperty("phasePercent").ValueKind);
    Assert.Equal(JsonValueKind.Null, line.RootElement.GetProperty("phaseSpeed").ValueKind);
  }

  [Fact]
  public async Task StaleRepeatedMeasurementSuppressesPhaseSpeed()
  {
    var clock = new ManualTimeProvider();
    var output = new StringWriter();
    await using var display = new CliProgressDisplay(output, true, clock);
    display.StartAttempt();
    var occurrence = Guid.NewGuid();
    display.Report(new(null, null, null, 0, Phase: ConversionPhase.Encoding,
      PhaseOccurrenceId: occurrence, IsPhaseEntry: true, ExpectsMeasurableUpdates: true));
    display.Report(new(null, null, "continue", 0, Phase: ConversionPhase.Encoding,
      PhaseOccurrenceId: occurrence, PhasePercent: 25, PhaseSpeed: 2));
    clock.Advance(TimeSpan.FromSeconds(11));
    display.Report(new(null, null, "continue", 0, Phase: ConversionPhase.Encoding,
      PhaseOccurrenceId: occurrence, PhasePercent: 25, PhaseSpeed: 2));
    await display.StopAttemptAsync();

    using var line = JsonDocument.Parse(Lines(output)[2]);
    Assert.Equal(25, line.RootElement.GetProperty("phasePercent").GetDouble());
    Assert.Equal(JsonValueKind.Null, line.RootElement.GetProperty("phaseSpeed").ValueKind);
  }

  [Fact]
  public async Task HumanOutputShowsCompletedSourcesWithoutNumericEvidence()
  {
    var output = new StringWriter();
    await using var display = new CliProgressDisplay(output, false);
    display.StartAttempt();
    var occurrence = Guid.NewGuid();
    display.Report(new(null, null, null, 0, Phase: ConversionPhase.Encoding,
      PhaseOccurrenceId: occurrence, IsPhaseEntry: true));
    display.Report(new(null, null, "continue", 0, Phase: ConversionPhase.Encoding,
      PhaseOccurrenceId: occurrence, CompletedSources: 1, TotalSources: 3));
    await display.StopAttemptAsync();

    Assert.Contains("Sources: 1/3", output.ToString());
    Assert.Contains("Numeric progress unavailable", output.ToString());
  }

  [Fact]
  public async Task HumanMeasuredProgressShowsPhaseSpeed()
  {
    var output = new StringWriter();
    await using var display = new CliProgressDisplay(output, false);
    display.StartAttempt();
    var occurrence = Guid.NewGuid();
    display.Report(new(null, null, null, 0, Phase: ConversionPhase.Encoding,
      PhaseOccurrenceId: occurrence, IsPhaseEntry: true));
    display.Report(new(null, null, "continue", 0, Phase: ConversionPhase.Encoding,
      PhaseOccurrenceId: occurrence, PhasePercent: 10, PhaseSpeed: 1.5));
    await display.StopAttemptAsync();

    Assert.Contains("Speed: 1.5×", output.ToString());
  }

  private static string[] Lines(StringWriter writer) => writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

  private static async Task UntilAsync(Func<bool> predicate)
  {
    for (var attempt = 0; attempt < 100 && !predicate(); attempt++) await Task.Delay(5);
    Assert.True(predicate(), "The expected timer output was not produced.");
  }

  internal sealed class ManualTimeProvider : TimeProvider
  {
    private long _timestamp;
    private readonly List<ManualTimer> _timers = [];
    public int ActiveTimerCount => _timers.Count(timer => !timer.IsDisposed);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
      var timer = new ManualTimer(callback, state);
      _timers.Add(timer);
      return timer;
    }
    public void Advance(TimeSpan duration)
    {
      _timestamp += duration.Ticks;
      foreach (var timer in _timers.ToArray()) timer.Fire();
    }
    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
      private bool _disposed;
      public bool IsDisposed => _disposed;
      public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
      public void Dispose() => _disposed = true;
      public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
      public void Fire() { if (!_disposed) callback(state); }
    }
  }

  private sealed record LegacyProgress(int SchemaVersion, string Event, double? DisplayPercent, double? Speed, int StageIndex, int? SourceIndex, string? State);
}
