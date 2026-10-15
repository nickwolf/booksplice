using BookSplice.Core.Execution;

namespace BookSplice.Core.Tests.Execution;

public sealed class ConversionProgressSessionTests
{
  [Fact]
  public void PhaseScopeStampsOneOccurrenceAndDiscardsReportsAfterDisposal()
  {
    var received = new RecordingProgress();
    using var session = new ConversionProgressSession(Guid.NewGuid(), received);
    using var operation = session.OpenOperation();
    var phase = ConversionPhaseScope.Start(operation, ConversionPhase.Encoding, expectsMeasurableUpdates: true);
    phase.Report(new ConversionProgress(null, null, "working", 0, PhasePercent: 20));
    phase.Dispose();
    phase.Report(new ConversionProgress(null, null, "late", 0, PhasePercent: 30));

    var events = received.Events;
    Assert.Equal(2, events.Length);
    Assert.True(events[0].IsPhaseEntry);
    Assert.True(events[0].ExpectsMeasurableUpdates);
    Assert.All(events, value => Assert.Equal(ConversionPhase.Encoding, value.Phase));
    Assert.All(events, value => Assert.Equal(events[0].PhaseOccurrenceId, value.PhaseOccurrenceId));
    Assert.NotEqual(Guid.Empty, events[0].PhaseOccurrenceId);
  }

  [Fact]
  public void NewOperationRetiresEarlierCallbacksBeforeStamping()
  {
    var received = new RecordingProgress();
    var jobId = Guid.NewGuid();
    using var session = new ConversionProgressSession(jobId, received);
    var oldOperation = session.OpenOperation();
    var oldPhase = ConversionPhaseScope.Start(oldOperation, ConversionPhase.Encoding);
    using var newOperation = session.OpenOperation();
    using var newPhase = ConversionPhaseScope.Start(newOperation, ConversionPhase.ValidationChecks);
    var count = received.Events.Length;
    oldPhase.Report(new ConversionProgress(null, null, "late", 0));
    oldOperation.Report(new ConversionProgress(null, null, "late legacy", 0));

    Assert.Equal(count, received.Events.Length);
    Assert.Equal([1L, 2L], received.Events.Select(value => value.Sequence));
    Assert.All(received.Events, value => Assert.Equal(jobId, value.JobId));
  }

  [Fact]
  public void SamePhaseReentryGetsNewOccurrenceAndRetiresPriorOne()
  {
    var received = new RecordingProgress();
    using var session = new ConversionProgressSession(Guid.NewGuid(), received);
    using var operation = session.OpenOperation();
    using var first = ConversionPhaseScope.Start(operation, ConversionPhase.Cleanup);
    using var second = ConversionPhaseScope.Start(operation, ConversionPhase.Cleanup);
    first.Report(new ConversionProgress(null, null, "late", 0));
    second.Report(new ConversionProgress(null, null, "current", 0));

    var events = received.Events;
    Assert.Equal(3, events.Length);
    Assert.NotEqual(events[0].PhaseOccurrenceId, events[1].PhaseOccurrenceId);
    Assert.Equal(events[1].PhaseOccurrenceId, events[2].PhaseOccurrenceId);
    Assert.Equal([1L, 2L, 3L], events.Select(value => value.Sequence));
  }

  [Fact]
  public void DelayedPhaseEntryCannotReactivateRetiredOccurrence()
  {
    var received = new RecordingProgress();
    using var session = new ConversionProgressSession(Guid.NewGuid(), received);
    using var operation = session.OpenOperation();
    var first = Guid.NewGuid();
    var second = Guid.NewGuid();
    operation.Report(new ConversionProgress(null, null, null, 0, Phase: ConversionPhase.Encoding, PhaseOccurrenceId: first, IsPhaseEntry: true));
    operation.Report(new ConversionProgress(null, null, null, 0, Phase: ConversionPhase.MetadataWriting, PhaseOccurrenceId: second, IsPhaseEntry: true));
    operation.Report(new ConversionProgress(null, null, null, 0, Phase: ConversionPhase.Encoding, PhaseOccurrenceId: first, IsPhaseEntry: true));
    operation.Report(new ConversionProgress(null, null, "late", 0, Phase: ConversionPhase.Encoding, PhaseOccurrenceId: first));

    Assert.Equal(2, received.Events.Length);
    Assert.Equal([1L, 2L], received.Events.Select(value => value.Sequence));
  }

  [Fact]
  public void ConcurrentReportsHaveUniqueIncreasingDeliverySequences()
  {
    var received = new RecordingProgress();
    using var session = new ConversionProgressSession(Guid.NewGuid(), received);
    using var operation = session.OpenOperation();
    using var phase = ConversionPhaseScope.Start(operation, ConversionPhase.Encoding);
    Parallel.For(0, 100, index => phase.Report(new ConversionProgress(null, null, "working", index)));

    Assert.Equal(101, received.Events.Length);
    Assert.Equal(Enumerable.Range(1, 101).Select(value => (long?)value), received.Events.Select(value => value.Sequence));
  }

  [Fact]
  public void SessionDisposalClosesAllSinks()
  {
    var received = new RecordingProgress();
    var session = new ConversionProgressSession(Guid.NewGuid(), received);
    var operation = session.OpenOperation();
    var phase = ConversionPhaseScope.Start(operation, ConversionPhase.Encoding);
    session.Dispose();
    phase.Report(new ConversionProgress(null, null, "late", 0));
    operation.Report(new ConversionProgress(null, null, "late legacy", 0));
    Assert.Single(received.Events);
  }

  private sealed class RecordingProgress : IProgress<ConversionProgress>
  {
    private readonly List<ConversionProgress> _events = [];
    public ConversionProgress[] Events { get { lock (_events) return _events.ToArray(); } }
    public void Report(ConversionProgress value) { lock (_events) _events.Add(value); }
  }
}
