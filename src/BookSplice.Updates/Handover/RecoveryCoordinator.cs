using System.Runtime.Versioning;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Handover;

public enum RecoveryOutcomeKind { NothingToRecover, Recovered, UpdateInProgress }

[SupportedOSPlatform("windows")]
public sealed class RecoveryCoordinator(UpdateJournal journal, IActivityLeaseStore activity)
{
  public RecoveryOutcomeKind Recover()
  {
    if (journal.Read().Value is not { } transaction || transaction.IsFinished())
      return RecoveryOutcomeKind.NothingToRecover;
    using var admission = activity.TryEnterUpdate();
    if (admission is null) return RecoveryOutcomeKind.UpdateInProgress;
    if (journal.Read().Value is { } current && !current.IsFinished())
      journal.Transition(current.Id, current.Phase, UpdatePhase.Failed);
    return RecoveryOutcomeKind.Recovered;
  }
}
