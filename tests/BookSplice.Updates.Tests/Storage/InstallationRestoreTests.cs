using BookSplice.Updates.Storage;

namespace BookSplice.Updates.Tests.Storage;

[Collection("WindowsOwnedStorage")]
public sealed class InstallationRestoreTests
{
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public void PersistedRootIdentityRejectsReplacement(bool replaceOriginal)
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-installation-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var original = Path.Combine(root, "BookSplice");
      var data = Path.Combine(root, "data");
      Directory.CreateDirectory(original);
      Directory.CreateDirectory(data);
      File.WriteAllText(Path.Combine(original, "BookSplice.Gui.exe"), "fixture");
      var installationId = Guid.NewGuid();
      var store = new OwnedDirectoryStore(root, _ => { });
      var managed = store.CreateNamedExclusive(root,
        ".booksplice-managed-" + installationId.ToString("D"), "managed-root",
        installationId, Guid.NewGuid());
      var versions = store.CreateNamedExclusive(managed, "versions", "versions",
        installationId, Guid.NewGuid());
      var staging = store.CreateNamedExclusive(managed, "staging", "staging",
        installationId, Guid.NewGuid());
      var trash = store.CreateNamedExclusive(managed, "trash", "trash",
        installationId, Guid.NewGuid());
      var layout = new InstallationLayout(original, data, managed, versions, staging, trash, store);
      var record = InstallationRecord.FromLayout(layout, "1.0.0");
      var replaced = replaceOriginal ? original : data;
      Directory.Move(replaced, replaced + "-old");
      Directory.CreateDirectory(replaced);
      if (replaceOriginal)
        File.WriteAllText(Path.Combine(replaced, "BookSplice.Gui.exe"), "fixture");

      Assert.Throws<OwnedDirectoryException>(() => record.Restore(store));
    }
    finally { Directory.Delete(root, true); }
  }
}
