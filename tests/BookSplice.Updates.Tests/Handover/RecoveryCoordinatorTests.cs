using BookSplice.Updates.Handover;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Handover;

[Collection("WindowsOwnedStorage")]
public sealed class RecoveryCoordinatorTests
{
  private static readonly FileIdentity Identity = new(1, "0123456789ABCDEF0123456789ABCDEF");

  [Theory]
  [InlineData(UpdatePhase.Preparing)]
  [InlineData(UpdatePhase.Prepared)]
  [InlineData(UpdatePhase.Starting)]
  public void UnfinishedPhaseBecomesFailedOnlyUnderAdmission(UpdatePhase phase)
  {
    WithJournal(phase, journal =>
    {
      var busy = new TrackingLeaseStore(() => true);
      Assert.Equal(RecoveryOutcomeKind.UpdateInProgress,
        new RecoveryCoordinator(journal, busy).Recover());
      Assert.Equal(phase, journal.Read().Value!.Phase);

      var admission = new TrackingLeaseStore();
      Assert.Equal(RecoveryOutcomeKind.Recovered,
        new RecoveryCoordinator(journal, admission).Recover());
      Assert.Equal(UpdatePhase.Failed, journal.Read().Value!.Phase);
      Assert.Equal(1, admission.Released);
      Assert.Equal(RecoveryOutcomeKind.NothingToRecover,
        new RecoveryCoordinator(journal, admission).Recover());
      Assert.Equal(1, admission.Entered);
    });
  }

  [Theory]
  [InlineData(UpdatePhase.Committed)]
  [InlineData(UpdatePhase.Failed)]
  public void FinishedJournalNeedsNoRecovery(UpdatePhase phase)
  {
    WithJournal(phase, journal =>
    {
      var admission = new TrackingLeaseStore();
      Assert.Equal(RecoveryOutcomeKind.NothingToRecover,
        new RecoveryCoordinator(journal, admission).Recover());
      Assert.Equal(phase, journal.Read().Value!.Phase);
      Assert.Equal(0, admission.Entered);
    });
  }

  private static void WithJournal(UpdatePhase phase, Action<UpdateJournal> test)
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-recovery-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var installation = Guid.NewGuid();
      var owned = new OwnedDirectoryStore(root, _ => { }).CreateExclusive(root,
        "managed", installation, Guid.NewGuid());
      var state = new AtomicStateStore(owned,
        [UpdateTransaction.Definition("journal.json", installation)]);
      var journal = new UpdateJournal(state, Path.Combine(owned.Path, "journal.json"), installation);
      var old = new OwnedOrOriginalTarget(TargetKind.Original,
        @"C:\BookSplice\BookSplice.Gui.exe", Identity, Identity, null, "1.0.0", 1, null);
      var id = Guid.NewGuid();
      journal.Begin(new UpdateTransaction(1, installation, id, UpdatePhase.Preparing, old, old,
        null, true, 1, new string('A', 64), [], null, null,
        new CurrentTarget(1, installation, old, null)));
      UpdatePhase[] path = phase switch
      {
        UpdatePhase.Preparing => [],
        UpdatePhase.Prepared => [UpdatePhase.Prepared],
        UpdatePhase.Starting => [UpdatePhase.Prepared, UpdatePhase.Starting],
        UpdatePhase.Committed => [UpdatePhase.Prepared, UpdatePhase.Starting, UpdatePhase.Committed],
        _ => [UpdatePhase.Failed]
      };
      var current = UpdatePhase.Preparing;
      foreach (var next in path)
      {
        journal.Transition(id, current, next);
        current = next;
      }
      test(journal);
    }
    finally { Directory.Delete(root, true); }
  }
}
