using BookSplice.Updates.Storage;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Updates.Tests.Storage;

[Collection("WindowsOwnedStorage")]
public sealed class OwnedDirectoryTests
{
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern SafeFileHandle CreateFileW(string path, uint desiredAccess, uint shareMode,
    nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

  private static int TryOpenForDataWrite(string path)
  {
    using var handle = CreateFileW(path, 0x2, 7, 0, 3, 0x02000000, 0);
    return handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
  }

  private static string NewRoot()
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-owned-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    return root;
  }

  [Fact]
  public void CollisionIsNeverRecordedOrRemoved()
  {
    var root = NewRoot();
    var suffix = new string('a', 32);
    var sentinel = Path.Combine(root, "staging-" + suffix);
    Directory.CreateDirectory(sentinel);
    File.WriteAllText(Path.Combine(sentinel, "sentinel.txt"), "keep");
    var recorded = 0;
    try
    {
      var store = new OwnedDirectoryStore(root, _ => recorded++, () => suffix);
      Assert.Throws<OwnedDirectoryException>(() => store.CreateExclusive(root, "staging", Guid.NewGuid(), Guid.NewGuid()));
      Assert.Equal(0, recorded);
      Assert.Equal("keep", File.ReadAllText(Path.Combine(sentinel, "sentinel.txt")));
    }
    finally
    {
      File.Delete(Path.Combine(sentinel, "sentinel.txt"));
      Directory.Delete(sentinel);
      Directory.Delete(root);
    }
  }

  [Fact]
  public void ReplacementAtRecordedPathIsRefused()
  {
    var root = NewRoot();
    try
    {
      var store = new OwnedDirectoryStore(root, _ => { });
      var owned = store.CreateExclusive(root, "staging", Guid.NewGuid(), Guid.NewGuid());
      Directory.Move(owned.Path, owned.Path + "-old");
      Directory.CreateDirectory(owned.Path);
      Assert.Throws<OwnedDirectoryException>(() => store.OpenValidated(owned));
      Directory.Delete(owned.Path);
      Directory.Delete(owned.Path + "-old");
    }
    finally
    {
      Directory.Delete(root);
    }
  }

  [Fact]
  public void IdentitySurvivesRename()
  {
    var root = NewRoot();
    try
    {
      var store = new OwnedDirectoryStore(root, _ => { });
      var owned = store.CreateExclusive(root, "staging", Guid.NewGuid(), Guid.NewGuid());
      var renamed = owned.Path + "-renamed";
      Directory.Move(owned.Path, renamed);
      using (var reopened = store.OpenValidated(owned with { Path = renamed }))
      {
        Assert.Equal(owned.Identity, reopened.Identity);
      }
      Directory.Delete(renamed);
    }
    finally
    {
      Directory.Delete(root);
    }
  }

  [Fact]
  public void AllowedRootReplacementIsRefused()
  {
    var root = NewRoot();
    var old = root + "-old";
    var store = new OwnedDirectoryStore(root, _ => { });
    Directory.Move(root, old);
    Directory.CreateDirectory(root);
    try
    {
      Assert.Throws<OwnedDirectoryException>(() => store.CreateExclusive(root, "staging", Guid.NewGuid(), Guid.NewGuid()));
    }
    finally
    {
      Directory.Delete(root);
      Directory.Delete(old);
    }
  }

  [Fact]
  public void OutsideRootAndInvalidIdentityAreRefused()
  {
    var root = NewRoot();
    var outside = NewRoot();
    try
    {
      var store = new OwnedDirectoryStore(root, _ => { });
      Assert.Throws<OwnedDirectoryException>(() => store.CreateExclusive(outside, "staging", Guid.NewGuid(), Guid.NewGuid()));
      Assert.Throws<OwnedDirectoryException>(() => store.CreateExclusive(root, "../bad", Guid.NewGuid(), Guid.NewGuid()));
      Assert.Throws<OwnedDirectoryException>(() => store.CreateExclusive(root, "staging", Guid.Empty, Guid.NewGuid()));
      var owned = store.CreateExclusive(root, "staging", Guid.NewGuid(), Guid.NewGuid());
      Assert.Throws<OwnedDirectoryException>(() => store.OpenValidated(owned with
      {
        Identity = new BookSplice.Updates.Windows.FileIdentity(owned.Identity.VolumeSerial, "invalid")
      }));
      Assert.Throws<OwnedDirectoryException>(() => store.OpenValidated(owned with { Path = root }));
      Directory.Delete(owned.Path);
    }
    finally
    {
      Directory.Delete(outside);
      Directory.Delete(root);
    }
  }

  [Fact]
  public void RecorderFailureLeavesOrphan()
  {
    var root = NewRoot();
    var suffix = new string('b', 32);
    var orphan = Path.Combine(root, "staging-" + suffix);
    try
    {
      var store = new OwnedDirectoryStore(root, _ => throw new InvalidOperationException("recorder failed"), () => suffix);
      Assert.Throws<OwnedDirectoryException>(() => store.CreateExclusive(root, "staging", Guid.NewGuid(), Guid.NewGuid()));
      Assert.True(Directory.Exists(orphan));
      Directory.Delete(orphan);
    }
    finally
    {
      Directory.Delete(root);
    }
  }

  [Fact]
  public void RetainedHandleBlocksReplacementAndAllowsChildCreation()
  {
    var root = NewRoot();
    try
    {
      var store = new OwnedDirectoryStore(root, _ => { });
      var owned = store.CreateExclusive(root, "staging", Guid.NewGuid(), Guid.NewGuid());
      using (var validated = store.OpenValidated(owned))
      {
        Assert.Throws<IOException>(() => Directory.Move(owned.Path, owned.Path + "-moved"));
        Directory.CreateDirectory(Path.Combine(owned.Path, "payload"));
        Assert.False(validated.Handle.IsInvalid);
      }

      Directory.Delete(Path.Combine(owned.Path, "payload"));
      Directory.Delete(owned.Path);
    }
    finally
    {
      Directory.Delete(root);
    }
  }

  [Fact]
  public void RecordedAncestryAndValidatedLeafDenyWriteOpen()
  {
    var root = NewRoot();
    var middle = Path.Combine(root, "middle");
    Directory.CreateDirectory(middle);
    var rootWriteError = 0;
    var middleWriteError = 0;
    OwnedDirectory? owned = null;
    try
    {
      var store = new OwnedDirectoryStore(root, _ =>
      {
        rootWriteError = TryOpenForDataWrite(root);
        middleWriteError = TryOpenForDataWrite(middle);
      });
      owned = store.CreateExclusive(middle, "staging", Guid.NewGuid(), Guid.NewGuid());
      Assert.Equal(32, rootWriteError);
      Assert.Equal(32, middleWriteError);
      using (var validated = store.OpenValidated(owned))
      {
        Assert.Equal(32, TryOpenForDataWrite(root));
        Assert.Equal(32, TryOpenForDataWrite(middle));
        Assert.Equal(32, TryOpenForDataWrite(owned.Path));
      }

    }
    finally
    {
      if (owned is not null && Directory.Exists(owned.Path)) Directory.Delete(owned.Path);
      if (Directory.Exists(middle)) Directory.Delete(middle);
      Directory.Delete(root);
    }
  }

  [Fact]
  public void AncestryReparsePointIsRefused()
  {
    var root = NewRoot();
    var outside = NewRoot();
    var link = Path.Combine(root, "link");
    try
    {
      using (var process = Process.Start(new ProcessStartInfo("cmd.exe")
      {
        ArgumentList = { "/d", "/c", "mklink", "/J", link, outside },
        UseShellExecute = false,
        CreateNoWindow = true
      })!)
      {
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
      }
      var store = new OwnedDirectoryStore(root, _ => { });
      Assert.Throws<OwnedDirectoryException>(() => store.CreateExclusive(link, "staging", Guid.NewGuid(), Guid.NewGuid()));
      Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }
    finally
    {
      if (Directory.Exists(link)) Directory.Delete(link);
      Directory.Delete(outside);
      Directory.Delete(root);
    }
  }
}
