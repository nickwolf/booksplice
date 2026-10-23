using System.Buffers.Binary;
using System.Runtime.InteropServices;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Updates.Tests.Windows;

// Folders above BookSplice's own (Downloads, AppData\Local) are held for whole updates and GUI runs,
// so other programs must still be able to rename files inside them.
[Collection("WindowsOwnedStorage")]
public sealed class AncestorSharingTests
{
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
    nint security, uint disposition, uint flags, nint template);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input,
    uint inputLength, nint output, uint outputLength, out uint returned, nint overlapped);

  [Fact]
  public void HeldFolderAboveOwnedAllowsRenameInsideButNotRenameOrJunction()
  {
    if (!OperatingSystem.IsWindows()) return;
    var root = Path.Combine(Path.GetTempPath(), "booksplice-sharing-" + Guid.NewGuid().ToString("N"));
    var downloads = Path.Combine(root, "Downloads");
    var control = Path.Combine(root, "empty-control");
    var outside = Path.Combine(root, "outside");
    Directory.CreateDirectory(downloads);
    Directory.CreateDirectory(control);
    Directory.CreateDirectory(outside);
    File.WriteAllText(Path.Combine(downloads, "book.part"), "partial");
    try
    {
      Directory.Move(downloads, downloads + "-moved");
      Directory.Move(downloads + "-moved", downloads);
      Assert.Equal(0, TrySetMountPoint(control, outside));
      Assert.Equal(0, ClearMountPoint(control));

      var store = new OwnedDirectoryStore(downloads, _ => { });
      var id = Guid.NewGuid();
      var managed = store.CreateNamedExclusive(downloads, InstallationOpener.Prefix + id.ToString("D"),
        "managed-root", id, Guid.NewGuid());
      var versions = store.CreateNamedExclusive(managed, "versions", "versions", id, Guid.NewGuid());
      File.WriteAllText(Path.Combine(managed.Path, "inside.tmp"), "owned");
      using var package = store.OpenValidated(versions);
      using var gui = GuiInstanceLease.TryAcquire(Path.Combine(downloads, "BookSplice"));
      Assert.NotNull(gui);
      using var job = new ActivityLeaseStore(Path.Combine(downloads, "BookSplice")).TryEnterJob();
      Assert.NotNull(job);

      File.Move(Path.Combine(downloads, "book.part"), Path.Combine(downloads, "book.m4b"));
      Assert.True(File.Exists(Path.Combine(downloads, "book.m4b")));
      Assert.Equal(32, Win32Code(Assert.ThrowsAny<IOException>(() =>
        Directory.Move(downloads, downloads + "-moved"))));
      // With the download moved out, only BookSplice's held children keep Downloads non-empty.
      File.Move(Path.Combine(downloads, "book.m4b"), Path.Combine(root, "book.m4b"));
      Assert.Equal(145, TrySetMountPoint(downloads, outside));
      Assert.Equal(0, (int)(File.GetAttributes(downloads) & FileAttributes.ReparsePoint));

      // The managed root is BookSplice's own, so holding it as an ancestor still keeps it read-only.
      Assert.Equal(32, Win32Code(Assert.ThrowsAny<IOException>(() =>
        File.Move(Path.Combine(managed.Path, "inside.tmp"), Path.Combine(managed.Path, "renamed.tmp")))));
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  private static int Win32Code(IOException error) => error.HResult & 0xFFFF;

  private static int TrySetMountPoint(string directory, string target)
  {
    var substitute = System.Text.Encoding.Unicode.GetBytes("\\??\\" + target);
    var print = System.Text.Encoding.Unicode.GetBytes(target);
    var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
    BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
    substitute.CopyTo(buffer, 16);
    print.CopyTo(buffer, 18 + substitute.Length);
    return Fsctl(directory, 0x000900A4, buffer);
  }

  private static int ClearMountPoint(string directory)
  {
    var buffer = new byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
    return Fsctl(directory, 0x000900AC, buffer);
  }

  private static int Fsctl(string directory, uint control, byte[] buffer)
  {
    using var handle = CreateFileW(directory, 0x100, 7, 0, 3, 0x02000000 | 0x00200000, 0);
    if (handle.IsInvalid) return Marshal.GetLastWin32Error();
    return DeviceIoControl(handle, control, buffer, (uint)buffer.Length, 0, 0, out _, 0) ?
      0 : Marshal.GetLastWin32Error();
  }
}
