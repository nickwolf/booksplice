using BookSplice.Updates.Handover;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Storage;

[Collection("WindowsOwnedStorage")]
public sealed class CurrentTargetStoreTests
{
  [Fact]
  public void ExactCommittingOperationReplacesAndRollbackRestoresThePointer()
  {
    if (!OperatingSystem.IsWindows()) return;
    var root = Path.Combine(Path.GetTempPath(), "booksplice-pointer-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var originalPath = Path.Combine(root, "BookSplice");
      var dataPath = Path.Combine(root, "data");
      Directory.CreateDirectory(dataPath);
      OriginalPackageFixture.Write(originalPath);
      var installation = Guid.NewGuid();
      var operation = Guid.NewGuid();
      var directories = new OwnedDirectoryStore(root, _ => { });
      var managed = directories.CreateNamedExclusive(root,
        ".booksplice-managed-" + installation.ToString("D"), "managed-root",
        installation, Guid.NewGuid());
      var records = directories.CreateNamedExclusive(managed, "records", "records",
        installation, Guid.NewGuid());
      var versions = directories.CreateNamedExclusive(managed, "versions", "versions",
        installation, Guid.NewGuid());
      var staging = directories.CreateNamedExclusive(managed, "staging", "staging",
        installation, Guid.NewGuid());
      var trash = directories.CreateNamedExclusive(managed, "trash", "trash",
        installation, Guid.NewGuid());
      var layout = new InstallationLayout(originalPath, dataPath, managed, records,
        versions, staging, trash, directories);
      var original = InstallationRecord.FromLayout(layout, "1.0.0");
      var oldTarget = new OwnedOrOriginalTarget(TargetKind.Original,
        original.OriginalExecutablePath, original.OriginalIdentity,
        original.OriginalExecutableIdentity, original.OriginalPackage,
        "1.0.0", 1, null);
      var leaf = "2.0.0-" + operation.ToString("D");
      var versionPath = Path.Combine(versions.Path, leaf);
      OriginalPackageFixture.Write(versionPath, "2.0.0");
      FileIdentity versionIdentity;
      using (var versionHandle = SafeDirectoryHandle.OpenPath(versionPath, false))
        versionIdentity = versionHandle.Identity;
      var owned = new OwnedDirectory(versionPath, versionIdentity,
        installation, operation, "version");
      PersistedPackage package;
      using (var captured = OriginalPackageInventory.Capture(versionPath,
        owned.Identity, "2.0.0"))
        package = PersistedPackage.FromSnapshot(captured.Snapshot);
      using var inventory = PackageInventory.ReopenSnapshot(owned, package.Restore(),
        directories, false);
      var executable = package.Files.Single(file => file.RelativePath == "BookSplice.Gui.exe");
      var newTarget = new OwnedOrOriginalTarget(TargetKind.Managed,
        Path.Combine(versionPath, "BookSplice.Gui.exe"), owned.Identity,
        executable.Identity, package, "2.0.0", 1, owned);
      var source = new OwnedDirectory(Path.Combine(staging.Path, "payload"),
        owned.Identity, installation, operation, "payload");
      var promotion = new PersistedPromotion(source, versions, leaf, versionPath,
        package, owned);
      var state = new AtomicStateStore(records,
      [
        CurrentTarget.Definition("current.json", installation),
        UpdateTransaction.Definition("journal.json", installation)
      ]);
      var pointerPath = Path.Combine(records.Path, "current.json");
      var previous = oldTarget with
      {
        ExecutablePath = Path.Combine(root, "Previous", "BookSplice.Gui.exe"),
        Version = "0.9.0",
        Package = null
      };
      var originalMapping = new CurrentTarget(1, installation, oldTarget, previous);
      state.Write(pointerPath, originalMapping);
      var journal = new UpdateJournal(state, Path.Combine(records.Path, "journal.json"),
        installation);
      var writer = new CurrentTargetStore(state, journal, layout);
      var recordedPrior = writer.CapturePriorMapping(oldTarget);
      var initial = new UpdateTransaction(1, installation, operation,
        UpdatePhase.Preparing, oldTarget, null, null, true, 1,
        new string('A', 64), [], null, null, null, promotion,
        PriorMapping: recordedPrior);
      Assert.False((initial with
      {
        PriorMapping = recordedPrior with { InstallationId = Guid.NewGuid() }
      }).IsValid());
      Assert.False((initial with
      {
        PriorMapping = recordedPrior with { Current = previous }
      }).IsValid());
      journal.Begin(initial);
      Assert.Throws<InvalidDataException>(() => journal.Change(operation,
        UpdatePhase.Preparing, current => current with
        {
          PriorMapping = originalMapping with { Previous = null }
        }));
      journal.Change(operation, UpdatePhase.Preparing,
        current => current with { NewTarget = newTarget });
      journal.Transition(operation, UpdatePhase.Preparing, UpdatePhase.Prepared);
      journal.Transition(operation, UpdatePhase.Prepared, UpdatePhase.Starting);
      journal.Transition(operation, UpdatePhase.Starting, UpdatePhase.Ready);
      journal.Transition(operation, UpdatePhase.Ready, UpdatePhase.Committing);
      Assert.Throws<InvalidDataException>(() => writer.Commit(Guid.NewGuid(), inventory));
      Assert.True(LaunchResolver.SameTarget(oldTarget,
        state.Read<CurrentTarget>(pointerPath).Value!.Current));
      state.Write(pointerPath, originalMapping with
      {
        Previous = previous with { Version = "0.8.0" }
      });
      Assert.Throws<InvalidDataException>(() => writer.Commit(operation, inventory));
      state.Write(pointerPath, originalMapping);
      var committed = writer.Commit(operation, inventory);
      Assert.True(LaunchResolver.SameTarget(newTarget, committed.Current));
      Assert.True(LaunchResolver.SameTarget(oldTarget, committed.Previous));
      Assert.Throws<InvalidDataException>(() => writer.Commit(operation, inventory));
      journal.Transition(operation, UpdatePhase.Committing, UpdatePhase.RollingBack);
      var restored = writer.Restore(operation);
      Assert.True(LaunchResolver.SameTarget(oldTarget, restored.Current));
      Assert.True(LaunchResolver.SameTarget(previous, restored.Previous));
      Assert.Throws<InvalidDataException>(() => writer.Restore(operation));
    }
    finally { Directory.Delete(root, true); }
  }
}
