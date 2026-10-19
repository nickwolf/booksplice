using BookSplice.Updates.Storage;

namespace BookSplice.Updates.Tests.Storage;

[Collection("WindowsOwnedStorage")]
public sealed class InstallationRestoreTests
{
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void PersistedRecordsChildRejectsMissingOrReplacement(bool replace)
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-records-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var original = Path.Combine(root, "BookSplice");
      var data = Path.Combine(root, "data");
      Directory.CreateDirectory(original);
      Directory.CreateDirectory(data);
      OriginalPackageFixture.Write(original);
      var id = Guid.NewGuid();
      var store = new OwnedDirectoryStore(root, _ => { });
      var managed = store.CreateNamedExclusive(root, ".booksplice-managed-" + id.ToString("D"),
        "managed-root", id, Guid.NewGuid());
      var records = store.CreateNamedExclusive(managed, "records", "records", id, Guid.NewGuid());
      var versions = store.CreateNamedExclusive(managed, "versions", "versions", id, Guid.NewGuid());
      var staging = store.CreateNamedExclusive(managed, "staging", "staging", id, Guid.NewGuid());
      var trash = store.CreateNamedExclusive(managed, "trash", "trash", id, Guid.NewGuid());
      var layout = new InstallationLayout(original, data, managed, records, versions, staging, trash, store);
      var record = InstallationRecord.FromLayout(layout, "1.0.0");
      Directory.Move(records.Path, records.Path + "-old");
      if (replace) Directory.CreateDirectory(records.Path);
      Assert.ThrowsAny<IOException>(() => record.Restore(store));
    }
    finally { Directory.Delete(root, true); }
  }

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
      OriginalPackageFixture.Write(original);
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
      var records = store.CreateNamedExclusive(managed, "records", "records",
        installationId, Guid.NewGuid());
      var layout = new InstallationLayout(original, data, managed, records, versions, staging, trash, store);
      var record = InstallationRecord.FromLayout(layout, "1.0.0");
      Assert.True(record.IsValid());
      Assert.NotNull(record.OriginalPackage);
      Assert.False((record with { OriginalPackageBindingSha256 = new string('0', 64) }).IsValid());
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
