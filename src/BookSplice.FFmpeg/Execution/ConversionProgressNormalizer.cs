using BookSplice.Core.Execution;
using BookSplice.Core.Planning;

namespace BookSplice.FFmpeg.Execution;

internal sealed class ConversionProgressNormalizer
{
  private readonly ConversionPlan _plan;
  private readonly LegacyDisplaySink? _sink;
  private readonly TimeProvider _clock;
  private readonly object _gate = new();
  private readonly long[] _legacyDurations;
  private readonly long[] _legacyProcessed;
  private readonly long[] _processed;
  private readonly bool[] _completed;
  private readonly Dictionary<(int Stage, int? Source), ProcessCallback> _processes = [];
  private readonly long? _totalDuration;
  private ConversionPhaseScope? _scope;
  private ConversionPhase? _phase;
  private long _phaseStarted;
  private double _legacyPercent;
  private double? _phaseSpeed;
  private double _lastPhasePercent;
  private int _completedSources;
  private long _singleProcessed;

  public ConversionProgressNormalizer(ConversionPlan plan, IProgress<ConversionProgress>? sink, TimeProvider? timeProvider = null)
  {
    _plan = plan;
    _sink = sink is null ? null : new LegacyDisplaySink(this, sink);
    _clock = timeProvider ?? TimeProvider.System;
    _totalDuration = SourceDurationEvidence.TotalMicroseconds(plan.SourceDurationsMicroseconds);
    _processed = new long[plan.SourcePaths.Count];
    _completed = new bool[plan.SourcePaths.Count];
    _legacyDurations = LegacyDurations(plan);
    _legacyProcessed = new long[_legacyDurations.Length];
  }

  public IDisposable BeginPhase(ConversionPhase phase)
  {
    lock (_gate)
    {
      _scope?.Dispose();
      _processes.Clear();
      _phase = phase;
      _phaseStarted = _clock.GetTimestamp();
      _phaseSpeed = null;
      _lastPhasePercent = 0;
      _singleProcessed = 0;
      Array.Clear(_processed);
      Array.Clear(_completed);
      _completedSources = 0;
      var measurable = IsMeasurable(phase);
      _scope = ConversionPhaseScope.Start(_sink, phase, measurable);
      return new PhaseLifetime(this, _scope);
    }
  }

  public IProgress<string> ForProcess(int stageIndex, int? sourceIndex)
  {
    lock (_gate)
    {
      var callback = new ProcessCallback(this, _scope, stageIndex, sourceIndex);
      _processes[(stageIndex, sourceIndex)] = callback;
      return callback;
    }
  }

  public void CompleteProcess(int stageIndex, int? sourceIndex)
  {
    lock (_gate)
    {
      if (!_processes.Remove((stageIndex, sourceIndex), out var callback) || callback.Scope != _scope) return;
      if (_phase == ConversionPhase.Encoding && _plan.Strategy == AudioStrategy.SegmentedTranscode && sourceIndex is { } index && index >= 0 && index < _completed.Length && !_completed[index])
      {
        _completed[index] = true;
        _completedSources++;
        if (_plan.SourceDurationsMicroseconds[index] is { } duration) _processed[index] = duration;
        _legacyProcessed[index] = _legacyDurations[index];
      }
      else if (_totalDuration is { } total && _phase is ConversionPhase.Encoding or ConversionPhase.StreamCopy or ConversionPhase.Assembly)
      {
        _singleProcessed = total;
      }
      var legacyDuration = LegacyDuration(sourceIndex);
      if (sourceIndex is null) _legacyPercent = Math.Max(_legacyPercent, Math.Clamp(LegacyFinalPercent(legacyDuration, legacyDuration), 0, 99));
      else _legacyPercent = Math.Max(_legacyPercent, Math.Clamp(LegacySegmentPercent(), 0, 99));
      Report(new ConversionProgress(legacyDuration, null, "end", stageIndex, sourceIndex, _legacyPercent));
    }
  }

  public void RetireProcess(int stageIndex, int? sourceIndex)
  {
    lock (_gate) _processes.Remove((stageIndex, sourceIndex));
  }

  public void CompleteExecution()
  {
    lock (_gate)
    {
      _scope?.Report(new ConversionProgress(null, null, "completed", int.MaxValue, DisplayPercent: 100));
      _scope?.Dispose();
      _scope = null;
      _phase = null;
      _processes.Clear();
    }
  }

  private void Process(ProcessCallback callback, string line)
  {
    var parsed = ConversionProgressParser.Parse(line, callback.StageIndex, callback.SourceIndex);
    if (parsed is null) return;
    lock (_gate)
    {
      if (callback.Scope is null || callback.Scope != _scope || !_processes.TryGetValue((callback.StageIndex, callback.SourceIndex), out var current) || current != callback) return;
      if (parsed.OutTimeMicroseconds is { } time)
      {
        if (_phase == ConversionPhase.Encoding && _plan.Strategy == AudioStrategy.SegmentedTranscode && callback.SourceIndex is { } index && index >= 0 && index < _processed.Length)
        {
          if (_plan.SourceDurationsMicroseconds[index] is { } duration) _processed[index] = Math.Max(_processed[index], Math.Clamp(time, 0, duration));
          _legacyProcessed[index] = Math.Max(_legacyProcessed[index], Math.Clamp(time, 0, _legacyDurations[index]));
        }
        else
        {
          if (_totalDuration is { } total) _singleProcessed = Math.Max(_singleProcessed, Math.Clamp(time, 0, total));
        }
      }
      var display = callback.SourceIndex is null ? LegacyFinalPercent(parsed.OutTimeMicroseconds, LegacyDuration(null)) : LegacySegmentPercent();
      _legacyPercent = Math.Max(_legacyPercent, Math.Clamp(display, 0, 99));
      Report(parsed with { DisplayPercent = _legacyPercent });
    }
  }

  private void Report(ConversionProgress value)
  {
    if (_scope is null) return;
    var percentage = PhasePercent();
    if (percentage is { } number)
    {
      _lastPhasePercent = Math.Max(_lastPhasePercent, number);
      percentage = _lastPhasePercent;
      var elapsed = _clock.GetElapsedTime(_phaseStarted, _clock.GetTimestamp()).TotalSeconds;
      var processedSeconds = AggregateProcessed() / 1_000_000d;
      if (elapsed > 0 && processedSeconds > 0)
      {
        var speed = processedSeconds / elapsed;
        if (double.IsFinite(speed) && speed > 0) _phaseSpeed = speed;
      }
    }
    _scope.Report(value with
    {
      PhasePercent = percentage,
      PhaseSpeed = percentage is null ? null : _phaseSpeed,
      ExpectsMeasurableUpdates = IsMeasurable(_phase),
      CompletedSources = _phase == ConversionPhase.Encoding && _plan.Strategy == AudioStrategy.SegmentedTranscode ? _completedSources : null,
      TotalSources = _phase == ConversionPhase.Encoding && _plan.Strategy == AudioStrategy.SegmentedTranscode ? _plan.SourcePaths.Count : null
    });
  }

  private bool IsMeasurable(ConversionPhase? phase) => _totalDuration is > 0 && phase is ConversionPhase.Encoding or ConversionPhase.StreamCopy or ConversionPhase.Assembly;
  private double? PhasePercent() => IsMeasurable(_phase) ? Math.Clamp(100d * AggregateProcessed() / _totalDuration!.Value, 0, 100) : null;
  private long AggregateProcessed() => _phase == ConversionPhase.Encoding && _plan.Strategy == AudioStrategy.SegmentedTranscode ? _processed.Sum() : _singleProcessed;
  private long? LegacyDuration(int? sourceIndex) => sourceIndex is { } index && index >= 0 && index < _legacyDurations.Length ? _legacyDurations[index] : _plan.Chapters.Count > 0 ? _plan.Chapters[^1].EndMicroseconds : null;
  private double LegacySegmentPercent() => _legacyDurations.Sum() == 0 ? _legacyPercent : 99d * _legacyProcessed.Zip(_legacyDurations).Sum(pair => Math.Min(pair.First, pair.Second)) / _legacyDurations.Sum();
  private double LegacyFinalPercent(long? time, long? duration) => duration is > 0 && time is >= 0 ? 99d * Math.Min(time.Value, duration.Value) / duration.Value : _legacyPercent;

  private static long[] LegacyDurations(ConversionPlan plan)
  {
    if (plan.SourcePaths.Count == 0) return [];
    if (plan.Chapters.Count == plan.SourcePaths.Count && plan.Chapters.All(chapter => chapter.EndMicroseconds > chapter.StartMicroseconds)) return plan.Chapters.Select(chapter => chapter.EndMicroseconds - chapter.StartMicroseconds).ToArray();
    return Enumerable.Repeat(1L, plan.SourcePaths.Count).ToArray();
  }

  private sealed class ProcessCallback(ConversionProgressNormalizer owner, ConversionPhaseScope? scope, int stageIndex, int? sourceIndex) : IProgress<string>
  {
    public ConversionPhaseScope? Scope { get; } = scope;
    public int StageIndex { get; } = stageIndex;
    public int? SourceIndex { get; } = sourceIndex;
    public void Report(string value) => owner.Process(this, value);
  }

  private sealed class LegacyDisplaySink(ConversionProgressNormalizer owner, IProgress<ConversionProgress> sink) : IProgress<ConversionProgress>
  {
    public void Report(ConversionProgress value) => sink.Report(value with { DisplayPercent = value.DisplayPercent ?? owner._legacyPercent });
  }

  private sealed class PhaseLifetime(ConversionProgressNormalizer owner, ConversionPhaseScope scope) : IDisposable
  {
    public void Dispose()
    {
      lock (owner._gate)
      {
        if (owner._scope != scope) return;
        scope.Dispose();
        owner._scope = null;
        owner._phase = null;
        owner._processes.Clear();
      }
    }
  }
}
