namespace BookSplice.Core.Execution;

public enum ProgressAdvisory { None, NoUpdate, NoAdvance }

public sealed record ConversionProgressSnapshot(
  double? Percent,
  double? Speed,
  TimeSpan? Remaining,
  TimeSpan Elapsed,
  TimeSpan? LastReportAge,
  TimeSpan? LastAdvanceAge,
  ProgressAdvisory Advisory,
  ConversionPhase? Phase,
  Guid? PhaseOccurrenceId,
  int? CompletedSources,
  int? TotalSources,
  Guid? JobId,
  long? Sequence);

public sealed class ConversionProgressObserver
{
  private static readonly TimeSpan AdvisoryDelay = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan NumericFreshness = TimeSpan.FromSeconds(10);
  private static readonly TimeSpan SampleWindow = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan MinimumObservation = TimeSpan.FromSeconds(3);

  private readonly object _gate = new();
  private readonly HashSet<Guid> _retiredOccurrences = [];
  private readonly List<(TimeSpan Elapsed, double Percent)> _samples = [];

  private ConversionPhase? _phase;
  private Guid? _phaseOccurrenceId;
  private Guid? _jobId;
  private long? _sequence;
  private double? _percent;
  private double? _speed;
  private bool _expectsMeasurableUpdates;
  private int? _completedSources;
  private int? _totalSources;
  private TimeSpan? _lastReport;
  private TimeSpan? _lastAdvance;

  public bool Report(ConversionProgress value, TimeSpan elapsed)
  {
    ArgumentNullException.ThrowIfNull(value);

    lock (_gate)
    {
      if (value.JobId is { } jobId && _jobId is { } currentJob && jobId != currentJob)
        return false;
      if (value.Sequence is { } sequence && _sequence is { } currentSequence && sequence <= currentSequence)
        return false;
      if (value.PhaseOccurrenceId is { } occurrence && _retiredOccurrences.Contains(occurrence))
        return false;

      if (value.IsPhaseEntry && value.Phase is { } nextPhase)
      {
        if (value.PhaseOccurrenceId == _phaseOccurrenceId && _phase is not null)
          return false;
        if (_phaseOccurrenceId is { } previousOccurrence)
          _retiredOccurrences.Add(previousOccurrence);
        _phase = nextPhase;
        _phaseOccurrenceId = value.PhaseOccurrenceId;
        _percent = null;
        _speed = null;
        _expectsMeasurableUpdates = value.ExpectsMeasurableUpdates;
        _completedSources = null;
        _totalSources = null;
        _samples.Clear();
        _lastAdvance = elapsed;
      }
      else if (value.Phase is not null &&
        (value.Phase != _phase || value.PhaseOccurrenceId != _phaseOccurrenceId))
      {
        return false;
      }

      _jobId ??= value.JobId;
      if (value.Sequence is not null)
        _sequence = value.Sequence;
      _lastReport = elapsed;

      if (value.Phase is null)
        return true;

      _completedSources = value.CompletedSources ?? _completedSources;
      _totalSources = value.TotalSources ?? _totalSources;

      if (value.PhasePercent is not { } measured || !double.IsFinite(measured) || measured < 0)
        return true;

      var percent = Math.Clamp(measured, 0, 100);
      if (_percent is { } previous && percent < previous)
        return true;

      var advanced = _percent is null ? percent > 0 : percent > _percent;
      if (_lastAdvance is { } lastAdvance && elapsed - lastAdvance > NumericFreshness)
        _samples.Clear();

      _percent = percent;
      if (advanced)
        _lastAdvance = elapsed;

      if (advanced || _samples.Count == 0)
        _samples.Add((elapsed, percent));
      _samples.RemoveAll(sample => elapsed - sample.Elapsed > SampleWindow);

      _speed = value.PhaseSpeed is { } speed && double.IsFinite(speed) && speed >= 0
        ? speed
        : null;
      return true;
    }
  }

  public ConversionProgressSnapshot Snapshot(TimeSpan elapsed)
  {
    lock (_gate)
    {
      var reportAge = Age(elapsed, _lastReport);
      var advanceAge = Age(elapsed, _lastAdvance);
      var advisory = reportAge >= AdvisoryDelay
        ? ProgressAdvisory.NoUpdate
        : _expectsMeasurableUpdates && advanceAge >= AdvisoryDelay
          ? ProgressAdvisory.NoAdvance
          : ProgressAdvisory.None;
      var fresh = advanceAge <= NumericFreshness;

      return new ConversionProgressSnapshot(
        _percent,
        fresh ? _speed : null,
        fresh ? EstimateRemaining(elapsed) : null,
        elapsed,
        reportAge,
        advanceAge,
        advisory,
        _phase,
        _phaseOccurrenceId,
        _completedSources,
        _totalSources,
        _jobId,
        _sequence);
    }
  }

  private TimeSpan? EstimateRemaining(TimeSpan elapsed)
  {
    if (_percent is null or >= 100 || _speed == 0 || _samples.Count < 2)
      return null;

    var first = _samples.FindIndex(sample => elapsed - sample.Elapsed <= SampleWindow);
    if (first < 0)
      return null;
    var earliest = _samples[first];
    var latest = _samples[^1];
    var observed = latest.Elapsed - earliest.Elapsed;
    if (observed < MinimumObservation || latest.Percent <= earliest.Percent)
      return null;

    var remainingSeconds = (100 - latest.Percent) * observed.TotalSeconds /
      (latest.Percent - earliest.Percent) - (elapsed - latest.Elapsed).TotalSeconds;
    return double.IsFinite(remainingSeconds) && remainingSeconds > 0 &&
      remainingSeconds <= TimeSpan.MaxValue.TotalSeconds
        ? TimeSpan.FromSeconds(remainingSeconds)
        : null;
  }

  private static TimeSpan? Age(TimeSpan now, TimeSpan? timestamp) =>
    timestamp is { } value ? TimeSpan.FromTicks(Math.Max(0, (now - value).Ticks)) : null;
}
