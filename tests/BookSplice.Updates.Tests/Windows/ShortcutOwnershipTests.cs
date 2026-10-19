using System.Security.Cryptography;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Tests.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Windows;

[Collection("WindowsOwnedStorage")]
public sealed class ShortcutOwnershipTests
{
  [Fact]
  public void UnownedCollisionSurvivesAndOwnedContentChangeIsNeverRepaired()
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-shortcut-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var installationId = Guid.NewGuid();
      var original = Path.Combine(root, "BookSplice");
      var data = Path.Combine(root, "data");
      var programs = Path.Combine(root, "Programs");
      Directory.CreateDirectory(data);
      Directory.CreateDirectory(programs);
      OriginalPackageFixture.Write(original);
      var directories = new OwnedDirectoryStore(root, _ => { });
      var managed = directories.CreateNamedExclusive(root,
        ".booksplice-managed-" + installationId, "managed-root", installationId,
        Guid.NewGuid());
      var versions = directories.CreateNamedExclusive(managed, "versions", "versions",
        installationId, Guid.NewGuid());
      var staging = directories.CreateNamedExclusive(managed, "staging", "staging",
        installationId, Guid.NewGuid());
      var trash = directories.CreateNamedExclusive(managed, "trash", "trash",
        installationId, Guid.NewGuid());
      var layout = new InstallationLayout(original, data, managed, versions, staging,
        trash, directories);
      File.WriteAllText(layout.StableLauncherPath, "launcher");
      FileIdentity launcherIdentity;
      using (var managedHandle = SafeDirectoryHandle.OpenPath(managed.Path, false))
      using (var launcher = managedHandle.OpenFile("BookSplice.Launcher.exe"))
        launcherIdentity = NativeFileIdentity.Read(launcher);
      var installation = InstallationRecord.FromLayout(layout, "1.0.0") with
      {
        StableLauncherIdentity = launcherIdentity,
        StableLauncherSha256 = Convert.ToHexStringLower(SHA256.HashData(
          File.ReadAllBytes(layout.StableLauncherPath)))
      };
      var path = Path.Combine(programs, "BookSplice.lnk");
      var recordPath = Path.Combine(managed.Path, "shortcut.json");
      var state = new AtomicStateStore(managed,
        [ShortcutOwnershipRecord.Definition("shortcut.json", installationId,
          path, layout.StableLauncherPath)]);
      var shortcuts = new ManagedShortcutStore(installation, state, recordPath, programs);
      File.WriteAllText(path, "unowned");
      Assert.Equal(ShortcutResult.Collision, shortcuts.EnsureRecordedShortcut(layout));
      Assert.Equal("unowned", File.ReadAllText(path));
      File.Delete(path);
      Assert.Equal(ShortcutResult.Created, shortcuts.EnsureRecordedShortcut(layout));
      var parsed = ManagedShortcutStore.InspectLink(path);
      Assert.Equal(layout.StableLauncherPath, parsed.Target, ignoreCase: true);
      Assert.Equal("", parsed.Arguments);
      Assert.Equal(ShortcutResult.Unchanged, shortcuts.EnsureRecordedShortcut(layout));
      var ownedBytes = File.ReadAllBytes(path);
      File.WriteAllBytes(path, [.. ownedBytes, 0x42]);
      Assert.Equal(ShortcutResult.Collision, shortcuts.EnsureRecordedShortcut(layout));
      Assert.Equal(ownedBytes.Length + 1, new FileInfo(path).Length);
    }
    finally { Directory.Delete(root, true); }
  }
}
