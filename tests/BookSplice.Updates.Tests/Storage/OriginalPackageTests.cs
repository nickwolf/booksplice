using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;
using System.Diagnostics;

namespace BookSplice.Updates.Tests.Storage;

[Collection("WindowsOwnedStorage")]
public sealed class OriginalPackageTests
{
  [Fact]
  public void CaptureAndReopenRequireTheExactReadOnlyPackage()
  {
    var parent = Path.Combine(Path.GetTempPath(), "booksplice-original-" + Guid.NewGuid().ToString("N"));
    var root = Path.Combine(parent, "BookSplice");
    OriginalPackageFixture.Write(root);
    try
    {
      using var original = SafeDirectoryHandle.OpenPath(root, false);
      using var capture = OriginalPackageInventory.Capture(root, original.Identity, "1.0.0");
      var persisted = PersistedPackage.FromSnapshot(capture.Snapshot);
      Assert.Equal(original.Identity, persisted.RootIdentity);
      Assert.Contains(capture.Snapshot.Files, x => x.RelativePath == "tools/ffmpeg/ffmpeg.exe");
      Assert.ThrowsAny<Exception>(() => File.WriteAllText(Path.Combine(root, "README.md"), "changed"));
      Assert.ThrowsAny<Exception>(() => Directory.Move(root, root + "-moved"));
      capture.Revalidate();
      using var reopened = OriginalPackageInventory.Reopen(root, capture.ParentIdentity,
        persisted, "1.0.0");
      Assert.Equal(persisted.Sha256, reopened.Snapshot.Sha256);
    }
    finally { Directory.Delete(parent, true); }
  }

  [Fact]
  public void ReopenRejectsAddedRemovedOrChangedBytes()
  {
    var parent = Path.Combine(Path.GetTempPath(), "booksplice-original-" + Guid.NewGuid().ToString("N"));
    var root = Path.Combine(parent, "BookSplice");
    OriginalPackageFixture.Write(root);
    try
    {
      using var identity = SafeDirectoryHandle.OpenPath(root, false);
      FileIdentity parentId;
      PersistedPackage package;
      using (var capture = OriginalPackageInventory.Capture(root, identity.Identity, "1.0.0"))
      { parentId = capture.ParentIdentity; package = PersistedPackage.FromSnapshot(capture.Snapshot); }
      var added = Path.Combine(root, "added.txt");
      File.WriteAllText(added, "new");
      Assert.Throws<PackageException>(() => OriginalPackageInventory.Reopen(root, parentId, package, "1.0.0"));
      File.Delete(added);
      File.Delete(Path.Combine(root, "LICENSE"));
      Assert.ThrowsAny<Exception>(() => OriginalPackageInventory.Reopen(root, parentId, package, "1.0.0"));
      File.WriteAllText(Path.Combine(root, "LICENSE"), "LICENSE");
      Assert.Throws<PackageException>(() => OriginalPackageInventory.Reopen(root, parentId, package, "1.0.0"));
    }
    finally { Directory.Delete(parent, true); }
  }

  [Fact]
  public void ReopenRejectsInPlaceBytesWithUnchangedFileIdentity()
  {
    var parent = Path.Combine(Path.GetTempPath(), "booksplice-original-" + Guid.NewGuid().ToString("N"));
    var root = Path.Combine(parent, "BookSplice");
    OriginalPackageFixture.Write(root);
    try
    {
      FileIdentity parentId;
      PersistedPackage package;
      using (var identity = SafeDirectoryHandle.OpenPath(root, false))
      using (var capture = OriginalPackageInventory.Capture(root, identity.Identity, "1.0.0"))
      { parentId = capture.ParentIdentity; package = PersistedPackage.FromSnapshot(capture.Snapshot); }
      var licensePath = Path.Combine(root, "LICENSE");
      File.WriteAllText(licensePath, "LICENSe");
      using (var reopenedRoot = SafeDirectoryHandle.OpenPath(root, false))
      using (var license = reopenedRoot.OpenFile("LICENSE"))
        Assert.Equal(package.Files.Single(x => x.RelativePath == "LICENSE").Identity,
          NativeFileIdentity.Read(license));
      Assert.Throws<PackageException>(() => OriginalPackageInventory.Reopen(root,
        parentId, package, "1.0.0"));
    }
    finally { Directory.Delete(parent, true); }
  }

  [Fact]
  public void CaptureRejectsMissingPayloadAndVersionMismatch()
  {
    var parent = Path.Combine(Path.GetTempPath(), "booksplice-original-" + Guid.NewGuid().ToString("N"));
    var root = Path.Combine(parent, "BookSplice");
    OriginalPackageFixture.Write(root);
    try
    {
      using var identity = SafeDirectoryHandle.OpenPath(root, false);
      Assert.Throws<PackageException>(() => OriginalPackageInventory.Capture(root, identity.Identity, "2.0.0"));
      File.Delete(Path.Combine(root, "booksplice.exe"));
      Assert.Throws<PackageException>(() => OriginalPackageInventory.Capture(root, identity.Identity, "1.0.0"));
      Assert.True(File.Exists(Path.Combine(root, "README.md")));
    }
    finally { Directory.Delete(parent, true); }
  }

  [Fact]
  public void CaptureRejectsToolHashMismatchAndDirectoryLink()
  {
    var parent = Path.Combine(Path.GetTempPath(), "booksplice-original-" + Guid.NewGuid().ToString("N"));
    var root = Path.Combine(parent, "BookSplice");
    OriginalPackageFixture.Write(root);
    try
    {
      using var identity = SafeDirectoryHandle.OpenPath(root, false);
      File.WriteAllText(Path.Combine(root, "tools", "ffmpeg", "ffmpeg.exe"), "different");
      Assert.Throws<PackageException>(() => OriginalPackageInventory.Capture(root, identity.Identity, "1.0.0"));
      OriginalPackageFixture.Write(root);
      var outside = Path.Combine(parent, "outside");
      Directory.CreateDirectory(outside);
      using (var process = Process.Start(new ProcessStartInfo("cmd.exe")
      {
        ArgumentList = { "/d", "/c", "mklink", "/J", Path.Combine(root, "linked"), outside },
        UseShellExecute = false,
        CreateNoWindow = true
      })!)
      {
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
      }
      Assert.Throws<PackageException>(() => OriginalPackageInventory.Capture(root, identity.Identity, "1.0.0"));
    }
    finally
    {
      var link = Path.Combine(root, "linked");
      if (Directory.Exists(link)) Directory.Delete(link);
      Directory.Delete(parent, true);
    }
  }

  [Fact]
  public void CaptureRejectsHardLinkWithoutChangingOutsideFile()
  {
    var parent = Path.Combine(Path.GetTempPath(), "booksplice-original-" + Guid.NewGuid().ToString("N"));
    var root = Path.Combine(parent, "BookSplice");
    OriginalPackageFixture.Write(root);
    var outside = Path.Combine(parent, "outside.txt");
    File.WriteAllText(outside, "outside bytes");
    try
    {
      var link = Path.Combine(root, "linked.txt");
      using (var process = Process.Start(new ProcessStartInfo("cmd.exe")
      {
        ArgumentList = { "/d", "/c", "mklink", "/H", link, outside },
        UseShellExecute = false,
        CreateNoWindow = true
      })!)
      {
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
      }
      using var identity = SafeDirectoryHandle.OpenPath(root, false);
      Assert.Throws<PackageException>(() => OriginalPackageInventory.Capture(root,
        identity.Identity, "1.0.0"));
      Assert.Equal("outside bytes", File.ReadAllText(outside));
      Assert.True(File.Exists(link));
    }
    finally { Directory.Delete(parent, true); }
  }

  [Fact]
  public void ReopenRejectsParentReplacementAndPersistedDigestTamper()
  {
    var outer = Path.Combine(Path.GetTempPath(), "booksplice-original-" + Guid.NewGuid().ToString("N"));
    var parent = Path.Combine(outer, "app-parent");
    var root = Path.Combine(parent, "BookSplice");
    OriginalPackageFixture.Write(root);
    try
    {
      FileIdentity parentId;
      PersistedPackage package;
      using (var identity = SafeDirectoryHandle.OpenPath(root, false))
      using (var capture = OriginalPackageInventory.Capture(root, identity.Identity, "1.0.0"))
      { parentId = capture.ParentIdentity; package = PersistedPackage.FromSnapshot(capture.Snapshot); }
      Assert.Throws<InvalidDataException>(() => OriginalPackageInventory.Reopen(root,
        parentId, package with { Sha256 = new string('0', 64) }, "1.0.0"));
      Directory.Move(parent, parent + "-old");
      Directory.CreateDirectory(parent);
      Directory.Move(Path.Combine(parent + "-old", "BookSplice"), root);
      using (var moved = SafeDirectoryHandle.OpenPath(root, false))
        Assert.Equal(package.RootIdentity, moved.Identity);
      Assert.ThrowsAny<Exception>(() => OriginalPackageInventory.Reopen(root,
        parentId, package, "1.0.0"));
      Assert.True(File.Exists(Path.Combine(root, "README.md")));
    }
    finally { Directory.Delete(outer, true); }
  }

  [Fact]
  public void ReopenRejectsRootOrFileIdentityReplacement()
  {
    var parent = Path.Combine(Path.GetTempPath(), "booksplice-original-" + Guid.NewGuid().ToString("N"));
    var root = Path.Combine(parent, "BookSplice");
    OriginalPackageFixture.Write(root);
    try
    {
      FileIdentity parentId;
      PersistedPackage package;
      using (var identity = SafeDirectoryHandle.OpenPath(root, false))
      using (var capture = OriginalPackageInventory.Capture(root, identity.Identity, "1.0.0"))
      { parentId = capture.ParentIdentity; package = PersistedPackage.FromSnapshot(capture.Snapshot); }
      var readme = Path.Combine(root, "README.md");
      File.Move(readme, readme + ".old");
      File.WriteAllText(readme, "README.md");
      Assert.Throws<PackageException>(() => OriginalPackageInventory.Reopen(root,
        parentId, package, "1.0.0"));
      File.Delete(readme);
      File.Move(readme + ".old", readme);
      Directory.Move(root, root + "-old");
      OriginalPackageFixture.Write(root);
      Assert.Throws<PackageException>(() => OriginalPackageInventory.Reopen(root,
        parentId, package, "1.0.0"));
    }
    finally { Directory.Delete(parent, true); }
  }
}
