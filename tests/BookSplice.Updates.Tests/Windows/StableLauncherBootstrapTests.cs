using BookSplice.Updates.Storage;
using BookSplice.Updates.Tests.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Windows;

[Collection("WindowsOwnedStorage")]
public sealed class StableLauncherBootstrapTests
{
  [Fact]
  public void CopiesOnlyRecordedCurrentPayloadAndPreservesCollision()
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-bootstrap-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var id = Guid.NewGuid();
      var original = Path.Combine(root, "BookSplice");
      var data = Path.Combine(root, "data");
      Directory.CreateDirectory(data);
      OriginalPackageFixture.Write(original);
      var directories = new OwnedDirectoryStore(root, _ => { });
      var managed = directories.CreateNamedExclusive(root, ".booksplice-managed-" + id,
        "managed-root", id, Guid.NewGuid());
      var versions = directories.CreateNamedExclusive(managed, "versions", "versions", id,
        Guid.NewGuid());
      var staging = directories.CreateNamedExclusive(managed, "staging", "staging", id,
        Guid.NewGuid());
      var trash = directories.CreateNamedExclusive(managed, "trash", "trash", id,
        Guid.NewGuid());
      var layout = new InstallationLayout(original, data, managed, versions, staging,
        trash, directories);
      var installation = InstallationRecord.FromLayout(layout, "1.0.0");
      var current = new CurrentTarget(1, id,
        new OwnedOrOriginalTarget(TargetKind.Original, installation.OriginalExecutablePath,
          installation.OriginalIdentity, installation.OriginalExecutableIdentity,
          installation.OriginalPackage, installation.OriginalVersion,
          installation.OriginalProtocolVersion, null), null);
      var ownership = new OwnershipState(1, id, managed.Identity,
        new[] { managed, versions, staging, trash }.Select(OwnershipEntry.FromCreated).ToArray());
      var recordPath = Path.Combine(managed.Path, "installation.json");
      var state = new AtomicStateStore(managed,
        [InstallationRecord.Definition("installation.json", id)]);
      state.Write(recordPath, installation);
      JournalSnapshot Snapshot() => new(
        state.Read<InstallationRecord>(recordPath),
        new StateRead<CurrentTarget>(StateReadKind.Valid, current),
        new StateRead<UpdateTransaction>(StateReadKind.Missing),
        new StateRead<OwnershipState>(StateReadKind.Valid, ownership));
      File.WriteAllText(layout.StableLauncherPath, "unowned");
      Assert.Throws<IOException>(() => StableLauncherBootstrap.Ensure(layout, Snapshot(),
        state, recordPath));
      Assert.Equal("unowned", File.ReadAllText(layout.StableLauncherPath));
      File.Delete(layout.StableLauncherPath);
      var recorded = StableLauncherBootstrap.Ensure(layout, Snapshot(), state, recordPath);
      Assert.NotNull(recorded.StableLauncherIdentity);
      Assert.Equal(File.ReadAllBytes(Path.Combine(original, "BookSplice.Launcher.exe")),
        File.ReadAllBytes(layout.StableLauncherPath));
      Assert.Equal(recorded.StableLauncherIdentity,
        state.Read<InstallationRecord>(recordPath).Value!.StableLauncherIdentity);
      Assert.Equal(recorded.StableLauncherIdentity,
        StableLauncherBootstrap.Ensure(layout, Snapshot(), state, recordPath).StableLauncherIdentity);
    }
    finally { Directory.Delete(root, true); }
  }
}
