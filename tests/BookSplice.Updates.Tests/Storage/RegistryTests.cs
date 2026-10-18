using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Storage;

[Collection("WindowsOwnedStorage")]
public sealed class RegistryTests
{
  [Fact]
  public void RegistryRejectsExecutableRebindingAndIdentityMismatch()
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-registry-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var installationId = Guid.NewGuid();
      var original = Path.Combine(root, "BookSplice");
      var data = Path.Combine(root, "data");
      Directory.CreateDirectory(original);
      Directory.CreateDirectory(data);
      OriginalPackageFixture.Write(original);
      var creator = new OwnedDirectoryStore(root, _ => { });
      var owned = creator.CreateNamedExclusive(root,
        ".booksplice-managed-" + installationId.ToString("D"), "managed-root",
        installationId, Guid.NewGuid());
      var versions = creator.CreateNamedExclusive(owned, "versions", "versions",
        installationId, Guid.NewGuid());
      var staging = creator.CreateNamedExclusive(owned, "staging", "staging",
        installationId, Guid.NewGuid());
      var trash = creator.CreateNamedExclusive(owned, "trash", "trash",
        installationId, Guid.NewGuid());
      var layout = new InstallationLayout(original, data, owned, versions, staging, trash, creator);
      var installation = InstallationRecord.FromLayout(layout, "1.0.0");
      var path = Path.Combine(owned.Path, "registry.json");
      var state = new AtomicStateStore(owned,
        [InstallationRegistryState.Definition("registry.json")]);
      var registry = new InstallationRegistry(state, path);
      var executable = installation.OriginalExecutableIdentity;
      var binding = new InstallationBinding(installation.OriginalExecutablePath,
        installation.OriginalIdentity, executable, installationId, TargetKind.Original,
        owned.Path, owned.Identity);
      registry.Bind(binding, installation);
      Assert.Equal(installationId,
        registry.Resolve(binding.ExecutablePath, installation.OriginalIdentity,
          executable)!.InstallationId);
      Assert.Throws<InvalidDataException>(() => registry.Resolve(binding.ExecutablePath,
        installation.OriginalIdentity, new FileIdentity(1, "22222222222222222222222222222222")));
      Assert.Throws<InvalidDataException>(() => registry.Bind(binding with
      { InstallationId = Guid.NewGuid() }, installation));
      Assert.Single(registry.Read().Value!.Bindings);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void RecorderDurablyBindsCreatedIdentity()
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-owner-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var installationId = Guid.NewGuid();
      var managed = new OwnedDirectoryStore(root, _ => { }).CreateExclusive(root,
        "managed", installationId, Guid.NewGuid());
      var path = Path.Combine(managed.Path, "ownership.json");
      var state = new AtomicStateStore(managed,
        [OwnershipState.Definition("ownership.json", managed)]);
      var recorder = new DurableOwnershipRecorder(state, path, managed);
      var creator = new OwnedDirectoryStore(root, recorder.Record);
      var child = creator.CreateNamedExclusive(managed, "versions", "versions",
        installationId, Guid.NewGuid());
      var read = state.Read<OwnershipState>(path);
      Assert.Equal(StateReadKind.Valid, read.Kind);
      var entry = Assert.Single(read.Value!.Entries);
      Assert.Equal(child.Identity, entry.Identity);
      Assert.Equal(child.Path, entry.Path);
    }
    finally { Directory.Delete(root, true); }
  }
}
