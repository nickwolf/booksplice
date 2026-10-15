namespace BookSplice.Core.Execution;

public sealed class ConversionProgressSession(Guid jobId, IProgress<ConversionProgress>? sink) : IDisposable
{
  private readonly object _gate = new();
  private OperationSink? _activeOperation;
  private Guid? _activeOccurrence;
  private readonly HashSet<Guid> _retiredOccurrences = [];
  private long _sequence;
  private bool _disposed;

  public OperationSink OpenOperation()
  {
    lock (_gate)
    {
      var operation = new OperationSink(this);
      if (_disposed) return operation;
      _activeOperation?.Close();
      _activeOperation = operation;
      if (_activeOccurrence is { } priorOccurrence) _retiredOccurrences.Add(priorOccurrence);
      _activeOccurrence = null;
      return operation;
    }
  }

  public void Dispose()
  {
    lock (_gate)
    {
      _disposed = true;
      _activeOperation?.Close();
      _activeOperation = null;
      if (_activeOccurrence is { } priorOccurrence) _retiredOccurrences.Add(priorOccurrence);
      _activeOccurrence = null;
    }
  }

  private void Report(OperationSink operation, ConversionProgress value)
  {
    lock (_gate)
    {
      if (_disposed || !ReferenceEquals(operation, _activeOperation) || operation.IsClosed) return;
      if (value.IsPhaseEntry)
      {
        if (value.Phase is null || value.PhaseOccurrenceId is not { } occurrence || occurrence == _activeOccurrence || _retiredOccurrences.Contains(occurrence)) return;
        if (_activeOccurrence is { } priorOccurrence) _retiredOccurrences.Add(priorOccurrence);
        _activeOccurrence = value.PhaseOccurrenceId;
      }
      else if (value.Phase is not null && (value.PhaseOccurrenceId is null || value.PhaseOccurrenceId != _activeOccurrence)) return;

      sink?.Report(value with { JobId = jobId, Sequence = ++_sequence });
    }
  }

  public sealed class OperationSink(ConversionProgressSession session) : IDisposable, IProgress<ConversionProgress>
  {
    internal bool IsClosed { get; private set; }
    internal void Close() => IsClosed = true;
    public void Report(ConversionProgress value) => session.Report(this, value);
    public void Dispose()
    {
      lock (session._gate)
      {
        IsClosed = true;
        if (ReferenceEquals(session._activeOperation, this))
        {
          session._activeOperation = null;
          if (session._activeOccurrence is { } priorOccurrence) session._retiredOccurrences.Add(priorOccurrence);
          session._activeOccurrence = null;
        }
      }
    }
  }
}
