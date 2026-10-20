using BookSplice.Updates.Handover;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Storage;

[Collection("WindowsOwnedStorage")]
public sealed class JournalTests
{
  private static readonly FileIdentity Identity = new(1, "0123456789ABCDEF0123456789ABCDEF");

  [Fact]
  public void StaleAndIllegalTransitionsRetainTheDurableRecord()
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-journal-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var installationId = Guid.NewGuid();
      var owned = new OwnedDirectoryStore(root, _ => { }).CreateExclusive(root,
        "managed", installationId, Guid.NewGuid());
      var path = Path.Combine(owned.Path, "journal.json");
      var state = new AtomicStateStore(owned,
        [UpdateTransaction.Definition("journal.json", installationId)]);
      var journal = new UpdateJournal(state, path, installationId);
      var old = new OwnedOrOriginalTarget(TargetKind.Original,
        @"C:\BookSplice\BookSplice.Gui.exe", Identity, Identity, null, "1.0.0", 1, null);
      var transaction = new UpdateTransaction(1, installationId, Guid.NewGuid(),
        UpdatePhase.Preparing, old, null, null, true, 1,
        new string('A', 64), [], null,
        new ProcessIdentity(42, 123, old.ExecutablePath), null, null);
      journal.Begin(transaction);
      var oldWriterMarker = new WriterFinalization(
        WriterFinalizationState.OldRestoredAfterCandidateExit, DateTime.UtcNow.Ticks,
        transaction.OldProcess!);
      Assert.False((transaction with { Finalization = oldWriterMarker }).IsValid());
      Assert.False((transaction with
      {
        Phase = UpdatePhase.Failed,
        Finalization = oldWriterMarker with
        {
          State = WriterFinalizationState.CandidateActiveAfterOldExit
        }
      }).IsValid());
      Assert.False((transaction with
      {
        Phase = UpdatePhase.Failed,
        Finalization = oldWriterMarker with
        {
          Process = new ProcessIdentity(43, 123, old.ExecutablePath)
        }
      }).IsValid());
      Assert.Throws<InvalidDataException>(() => journal.Begin(
        transaction with { Id = Guid.NewGuid(), Finalization = oldWriterMarker }));
      Assert.Throws<InvalidDataException>(() => journal.Begin(
        transaction with { Id = Guid.NewGuid(), Finalization = oldWriterMarker }, transaction.Id));

      Assert.Throws<InvalidDataException>(() => journal.Transition(transaction.Id,
        UpdatePhase.Preparing, UpdatePhase.Committed));
      Assert.Throws<InvalidDataException>(() => journal.Transition(Guid.NewGuid(),
        UpdatePhase.Preparing, UpdatePhase.RollingBack));
      Assert.Throws<InvalidDataException>(() => journal.Change(transaction.Id,
        UpdatePhase.Preparing, current => current with
        {
          SettingsAbsent = false,
          SettingsSha256 = new string('B', 64)
        }));
      Assert.Throws<InvalidDataException>(() => journal.Begin(transaction with { Id = Guid.NewGuid() }));
      Assert.Equal(UpdatePhase.Preparing, journal.Read().Value!.Phase);

      journal.Transition(transaction.Id, UpdatePhase.Preparing, UpdatePhase.RollingBack);
      journal.Transition(transaction.Id, UpdatePhase.RollingBack, UpdatePhase.Failed);
      Assert.Throws<InvalidDataException>(() => journal.Begin(transaction with { Id = Guid.NewGuid() }));
      Assert.Throws<InvalidDataException>(() => journal.Begin(
        transaction with { Id = Guid.NewGuid() }, Guid.NewGuid()));
      Assert.Throws<InvalidDataException>(() => journal.RecordObservedFinalization(
        transaction.Id, UpdatePhase.Failed,
        WriterFinalizationState.OldRestoredAfterCandidateExit,
        new ProcessIdentity(43, 123, old.ExecutablePath)));
      Assert.Throws<InvalidDataException>(() => journal.Change(transaction.Id,
        UpdatePhase.Failed, current => current with { Finalization = oldWriterMarker }));
      journal.RecordObservedFinalization(transaction.Id, UpdatePhase.Failed,
        WriterFinalizationState.OldRestoredAfterCandidateExit,
        transaction.OldProcess!);
      var nextProcess = new ProcessIdentity(44, 124, old.ExecutablePath);
      var next = transaction with
      {
        Id = Guid.NewGuid(),
        Phase = UpdatePhase.Preparing,
        NewTarget = old,
        NewProcess = nextProcess
      };
      Assert.Throws<InvalidDataException>(() => journal.Begin(next, Guid.NewGuid()));
      journal.Begin(next, transaction.Id);
      journal.Transition(next.Id, UpdatePhase.Preparing, UpdatePhase.Prepared);
      journal.Transition(next.Id, UpdatePhase.Prepared, UpdatePhase.Starting);
      journal.Transition(next.Id, UpdatePhase.Starting, UpdatePhase.Ready);
      journal.Transition(next.Id, UpdatePhase.Ready, UpdatePhase.Committing);
      journal.Transition(next.Id, UpdatePhase.Committing, UpdatePhase.Committed);
      journal.RecordObservedFinalization(next.Id, UpdatePhase.Committed,
        WriterFinalizationState.CandidateActiveAfterOldExit, nextProcess);
      Assert.True(journal.Read().Value!.IsValid());
      var final = transaction with { Id = Guid.NewGuid(), Phase = UpdatePhase.Preparing };
      journal.Begin(final, next.Id);
      Assert.Equal(final.Id, journal.Read().Value!.Id);
    }
    finally { Directory.Delete(root, true); }
  }
}
