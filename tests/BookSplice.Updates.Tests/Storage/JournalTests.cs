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
      journal.RecordObservedFinalization(transaction.Id, UpdatePhase.Failed,
        WriterFinalizationState.OldRestoredAfterCandidateExit,
        transaction.OldProcess!);
      var next = transaction with { Id = Guid.NewGuid(), Phase = UpdatePhase.Preparing };
      Assert.Throws<InvalidDataException>(() => journal.Begin(next, Guid.NewGuid()));
      journal.Begin(next, transaction.Id);
      Assert.Equal(next.Id, journal.Read().Value!.Id);
    }
    finally { Directory.Delete(root, true); }
  }
}
