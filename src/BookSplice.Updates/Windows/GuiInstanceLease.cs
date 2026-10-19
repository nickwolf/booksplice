using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Updates.Windows;

[SupportedOSPlatform("windows")]
public sealed class GuiInstanceLease : IDisposable
{
  private readonly SafeDirectoryHandle _root;
  private readonly FileStream _stream;
  private bool _disposed;

  private GuiInstanceLease(SafeDirectoryHandle root, FileStream stream)
    => (_root, _stream) = (root, stream);

  [StructLayout(LayoutKind.Sequential)]
  private struct Overlapped
  {
    public nuint Internal;
    public nuint InternalHigh;
    public uint Offset;
    public uint OffsetHigh;
    public nint Event;
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool LockFileEx(SafeFileHandle handle, uint flags, uint reserved, uint bytesLow, uint bytesHigh, ref Overlapped overlapped);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool UnlockFileEx(SafeFileHandle handle, uint reserved, uint bytesLow, uint bytesHigh, ref Overlapped overlapped);

  public static GuiInstanceLease? TryAcquire(string canonicalDataRoot)
    => TryAcquire(canonicalDataRoot, null);

  internal static GuiInstanceLease? TryAcquire(string canonicalDataRoot, Action? beforeLockChild)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDataRoot);
    var path = Path.GetFullPath(canonicalDataRoot);
    using var created = SafeDirectoryHandle.OpenPath(path, permitChildren: false,
      createMissing: true);
    beforeLockChild?.Invoke();
    var stream = new FileStream(created.OpenOrCreateFile("gui-instance.lock"), FileAccess.ReadWrite);
    SafeDirectoryHandle? root = null;
    try
    {
      root = SafeDirectoryHandle.OpenPath(path, permitChildren: false,
        allowEntryMutation: true);
      if (root.Identity != created.Identity)
        throw new IOException("The GUI instance data root changed during acquisition.");
      var overlapped = new Overlapped();
      if (!LockFileEx(stream.SafeFileHandle, 3, 0, 1, 0, ref overlapped))
      {
        var error = Marshal.GetLastWin32Error();
        if (error is 33 or 158) return null;
        throw new Win32Exception(error, "The GUI instance lock could not be acquired.");
      }
      var lease = new GuiInstanceLease(root, stream);
      root = null;
      stream = null!;
      return lease;
    }
    finally { stream?.Dispose(); root?.Dispose(); }
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    var overlapped = new Overlapped();
    try
    {
      if (!UnlockFileEx(_stream.SafeFileHandle, 0, 1, 0, ref overlapped))
        throw new Win32Exception(Marshal.GetLastWin32Error(), "The GUI instance lock could not be released.");
    }
    finally
    {
      _stream.Dispose();
      _root.Dispose();
    }
  }
}
