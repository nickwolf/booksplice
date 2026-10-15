namespace BookSplice.Core.Execution;

public sealed class ConversionPhaseScope : IDisposable, IProgress<ConversionProgress>
{
  private readonly IProgress<ConversionProgress>? _sink;
  private readonly ConversionPhase _phase;
  private readonly Guid _occurrenceId = Guid.NewGuid();
  private readonly object _gate = new();
  private bool _disposed;

  private ConversionPhaseScope(IProgress<ConversionProgress>? sink, ConversionPhase phase, bool expectsMeasurableUpdates)
  {
    _sink = sink;
    _phase = phase;
    _sink?.Report(new ConversionProgress(null, null, null, 0, Phase: phase, PhaseOccurrenceId: _occurrenceId, IsPhaseEntry: true, ExpectsMeasurableUpdates: expectsMeasurableUpdates));
  }

  public static ConversionPhaseScope Start(IProgress<ConversionProgress>? sink, ConversionPhase phase, bool expectsMeasurableUpdates = false)
    => new(sink, phase, expectsMeasurableUpdates);

  public void Report(ConversionProgress value)
  {
    lock (_gate)
    {
      if (_disposed) return;
      _sink?.Report(value with { Phase = _phase, PhaseOccurrenceId = _occurrenceId, IsPhaseEntry = false });
    }
  }

  public void Dispose()
  {
    lock (_gate) _disposed = true;
  }
}
