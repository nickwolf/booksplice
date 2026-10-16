using System.Globalization;
using System.Text.Json;
using BookSplice.Core.Execution;

namespace BookSplice.Cli;

public sealed class CliProgressDisplay(TextWriter output, bool json, TimeProvider? timeProvider = null) : IProgress<ConversionProgress>, IAsyncDisposable
{
  private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
  private readonly object _gate = new();
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
  private ConversionProgressObserver _observer = new();
  private CancellationTokenSource? _timerCancellation;
  private Task? _timerTask;
  private long _startedAt;
  private bool _accepting;
  private bool _reported;
  private ProgressAdvisory _lastAdvisory;
  private long _lastAdvisorySecond = -1;
  private double? _lastShownPercent;
  private string? _lastShownSources;
  private bool _expectsMeasurableUpdates;

  public void StartAttempt()
  {
    lock (_gate)
    {
      if (_timerTask is not null) throw new InvalidOperationException("A progress attempt is already active.");
      _observer = new ConversionProgressObserver();
      _startedAt = _timeProvider.GetTimestamp();
      _reported = false;
      _lastAdvisory = ProgressAdvisory.None;
      _lastAdvisorySecond = -1;
      _lastShownPercent = null;
      _lastShownSources = null;
      _expectsMeasurableUpdates = false;
      _accepting = true;
      _timerCancellation = new CancellationTokenSource();
      _timerTask = ObserveAsync(_timerCancellation.Token);
    }
  }

  public async Task StopAttemptAsync()
  {
    Task? timerTask;
    CancellationTokenSource? cancellation;
    lock (_gate)
    {
      _accepting = false;
      timerTask = _timerTask;
      cancellation = _timerCancellation;
      cancellation?.Cancel();
    }
    if (timerTask is not null) await timerTask.ConfigureAwait(false);
    lock (_gate)
    {
      if (ReferenceEquals(_timerTask, timerTask))
      {
        _timerTask = null;
        _timerCancellation = null;
      }
    }
    cancellation?.Dispose();
  }

  public ValueTask DisposeAsync() => new(StopAttemptAsync());

  public void Report(ConversionProgress value)
  {
    lock (_gate)
    {
      if (!_accepting) return;
      var elapsed = Elapsed();
      if (!_observer.Report(value, elapsed)) return;
      _reported = true;
      if (value.IsPhaseEntry) _expectsMeasurableUpdates = value.ExpectsMeasurableUpdates;
      var snapshot = _observer.Snapshot(elapsed);
      var recovered = _lastAdvisory != ProgressAdvisory.None && snapshot.Advisory == ProgressAdvisory.None;
      _lastAdvisory = snapshot.Advisory;
      if (json)
        WriteJson(value, snapshot, recovered ? "none" : null);
      else
        WriteHuman(value, snapshot, recovered);
    }
  }

  private async Task ObserveAsync(CancellationToken cancellationToken)
  {
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
    try
    {
      while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
      {
        lock (_gate)
        {
          if (!_accepting) continue;
          var elapsed = Elapsed();
          var snapshot = _observer.Snapshot(elapsed);
          var advisory = !_reported && elapsed >= TimeSpan.FromSeconds(30)
            ? ProgressAdvisory.NoUpdate : snapshot.Advisory;
          if (advisory == ProgressAdvisory.None) continue;
          var second = (long)elapsed.TotalSeconds;
          if (advisory == _lastAdvisory && second == _lastAdvisorySecond) continue;
          _lastAdvisory = advisory;
          _lastAdvisorySecond = second;
          if (json) WriteJson(null, snapshot, AdvisoryName(advisory), !_reported ? elapsed : null);
          else WriteAdvisory(advisory, !_reported ? elapsed : snapshot.LastReportAge, snapshot.LastAdvanceAge);
        }
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
  }

  private void WriteJson(ConversionProgress? value, ConversionProgressSnapshot snapshot, string? advisory, TimeSpan? initialReportAge = null)
  {
    var eventValue = new
    {
      SchemaVersion = 1,
      Event = "progress",
      DisplayPercent = value?.DisplayPercent,
      Speed = value?.Speed,
      StageIndex = value?.StageIndex,
      SourceIndex = value?.SourceIndex,
      State = value?.State,
      Phase = value?.Phase.ToString() ?? snapshot.Phase?.ToString(),
      PhasePercent = snapshot.Percent,
      PhaseSpeed = snapshot.Speed,
      MeasurementScope = "phase",
      JobId = value?.JobId ?? snapshot.JobId,
      Sequence = value?.Sequence ?? snapshot.Sequence,
      PhaseOccurrenceId = value?.PhaseOccurrenceId ?? snapshot.PhaseOccurrenceId,
      ExpectsMeasurableUpdates = snapshot.Phase is null ? (bool?)null : _expectsMeasurableUpdates,
      CompletedSources = value?.CompletedSources ?? snapshot.CompletedSources,
      TotalSources = value?.TotalSources ?? snapshot.TotalSources,
      Advisory = advisory,
      LastReportAgeSeconds = advisory is null ? null : (double?)(initialReportAge ?? snapshot.LastReportAge)?.TotalSeconds,
      LastAdvanceAgeSeconds = advisory is null ? null : (double?)snapshot.LastAdvanceAge?.TotalSeconds,
      ObservationElapsedSeconds = advisory is null ? null : (double?)snapshot.Elapsed.TotalSeconds
    };
    output.WriteLine(JsonSerializer.Serialize(eventValue, JsonOptions));
  }

  private void WriteHuman(ConversionProgress value, ConversionProgressSnapshot snapshot, bool recovered)
  {
    var sources = snapshot.CompletedSources is { } completed && snapshot.TotalSources is { } total
      ? FormattableString.Invariant($"Sources: {completed}/{total}") : null;
    if (value.IsPhaseEntry)
    {
      _lastShownPercent = null;
      _lastShownSources = sources;
      output.WriteLine($"{Label(snapshot.Phase)} · Elapsed: {Duration(snapshot.Elapsed)} · Numeric progress unavailable{(sources is null ? "" : " · " + sources)}");
    }
    else if (snapshot.Phase is null && value.DisplayPercent is { } legacyPercent)
      output.WriteLine($"Progress {legacyPercent.ToString("0.#", CultureInfo.InvariantCulture)}%");
    else if (snapshot.Phase is not null && snapshot.Percent is { } percent && (percent != _lastShownPercent || sources != _lastShownSources))
    {
      _lastShownPercent = percent;
      _lastShownSources = sources;
      var speed = snapshot.Speed is { } measuredSpeed ? " · Speed: " + measuredSpeed.ToString("0.0", CultureInfo.InvariantCulture) + "×" : "";
      output.WriteLine($"{Label(snapshot.Phase)} · {percent.ToString("0.#", CultureInfo.InvariantCulture)}%{speed} · Phase remaining: {(snapshot.Remaining is { } remaining ? Duration(remaining) : "Unavailable")}{(sources is null ? "" : " · " + sources)}");
    }
    else if (snapshot.Phase is not null && sources is not null && sources != _lastShownSources)
    {
      _lastShownSources = sources;
      output.WriteLine($"{Label(snapshot.Phase)} · Elapsed: {Duration(snapshot.Elapsed)} · Numeric progress unavailable · {sources}");
    }
    if (recovered) output.WriteLine($"{Label(snapshot.Phase)} · Updates resumed");
  }

  private void WriteAdvisory(ProgressAdvisory advisory, TimeSpan? reportAge, TimeSpan? advanceAge)
  {
    if (advisory == ProgressAdvisory.NoUpdate && reportAge is { } noReport)
      output.WriteLine($"No update received for {Duration(noReport)}; operation has not returned");
    else if (advisory == ProgressAdvisory.NoAdvance && advanceAge is { } noAdvance)
      output.WriteLine($"Updates received, but progress has not advanced for {Duration(noAdvance)}");
  }

  private TimeSpan Elapsed() => _timeProvider.GetElapsedTime(_startedAt);

  private static string AdvisoryName(ProgressAdvisory advisory) => advisory switch
  {
    ProgressAdvisory.NoUpdate => "noUpdate",
    ProgressAdvisory.NoAdvance => "noAdvance",
    _ => "none"
  };

  private static string Label(ConversionPhase? phase) => phase switch
  {
    ConversionPhase.Analysis => "Analyzing sources",
    ConversionPhase.Planning => "Planning conversion",
    ConversionPhase.Preparation => "Preparing conversion",
    ConversionPhase.Encoding => "Encoding audio",
    ConversionPhase.StreamCopy => "Copying audio",
    ConversionPhase.Assembly => "Assembling audio",
    ConversionPhase.MetadataWriting => "Writing metadata",
    ConversionPhase.ValidationChecks => "Checking output",
    ConversionPhase.FullDecode => "Validating full audio",
    ConversionPhase.Publication => "Publishing output",
    ConversionPhase.Cleanup => "Cleaning up",
    ConversionPhase.AuditWriting => "Writing audit",
    _ => "Starting conversion"
  };

  private static string Duration(TimeSpan duration)
  {
    var seconds = (long)Math.Max(0, duration.TotalSeconds);
    return seconds >= 3600 ? FormattableString.Invariant($"{seconds / 3600}h {seconds / 60 % 60}m {seconds % 60}s")
      : seconds >= 60 ? FormattableString.Invariant($"{seconds / 60}m {seconds % 60}s")
      : FormattableString.Invariant($"{seconds}s");
  }
}
