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
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDataRoot);
    var path = Path.GetFullPath(canonicalDataRoot);
    var root = SafeDirectoryHandle.OpenPath(path, permitChildren: false, createMissing: true);
    try
    {
      var stream = new FileStream(root.OpenOrCreateFile("gui-instance.lock"), FileAccess.ReadWrite);
      try
      {
        var overlapped = new Overlapped();
        if (!LockFileEx(stream.SafeFileHandle, 3, 0, 1, 0, ref overlapped))
        {
          var error = Marshal.GetLastWin32Error();
          if (error is 33 or 158) return null;
          throw new Win32Exception(error, "The GUI instance lock could not be acquired.");
        }
        var lease = new GuiInstanceLease(root, stream);
        root = null!;
        stream = null!;
        return lease;
      }
      finally { stream?.Dispose(); }
    }
    finally { root?.Dispose(); }
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
