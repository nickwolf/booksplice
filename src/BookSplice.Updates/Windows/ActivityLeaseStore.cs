using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Updates.Windows;

public interface IActivityLeaseStore
{
  IDisposable? TryEnterJob();
  IDisposable? TryEnterUpdate();
}

public sealed class NoOpActivityLeaseStore : IActivityLeaseStore
{
  public IDisposable TryEnterJob() => new NoOpLease();
  public IDisposable TryEnterUpdate() => new NoOpLease();

  private sealed class NoOpLease : IDisposable
  {
    public void Dispose() { }
  }
}

[SupportedOSPlatform("windows")]
public sealed class ActivityLeaseStore : IActivityLeaseStore
{
  private readonly string _root;
  private readonly FileIdentity _identity;

  public ActivityLeaseStore(string canonicalDataRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDataRoot);
    _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalDataRoot));
    using var root = OpenRootUnanchored();
    _identity = root.Identity;
  }

  public IDisposable? TryEnterJob()
  {
    var root = OpenRoot();
    var transferred = false;
    try
    {
      var gate = TryLock(root, 0, exclusive: false);
      if (gate is null) return null;
      NativeLease? job = null;
      try
      {
        job = TryLock(root, 1, exclusive: false, gate.Identity);
      }
      catch
      {
        gate.Dispose();
        throw;
      }
      try { gate.Dispose(); }
      catch
      {
        job?.Dispose();
        throw;
      }
      if (job is null) return null;
      transferred = true;
      return new RootedLease(root, job);
    }
    finally { if (!transferred) root.Dispose(); }
  }

  public IDisposable? TryEnterUpdate()
  {
    var root = OpenRoot();
    var transferred = false;
    try
    {
      var gate = TryLock(root, 0, exclusive: true);
      if (gate is null) return null;
      try
      {
        var jobs = TryLock(root, 1, exclusive: true, gate.Identity);
        if (jobs is null) return null;
        gate.Transferred = true;
        transferred = true;
        return new RootedLease(root, new CombinedLease(gate, jobs));
      }
      finally { if (!gate.Transferred) gate.Dispose(); }
    }
    finally { if (!transferred) root.Dispose(); }
  }

  private SafeDirectoryHandle OpenRoot()
  {
    var root = SafeDirectoryHandle.OpenPath(_root, permitChildren: false);
    if (root.Identity == _identity) return root;
    root.Dispose();
    throw new InvalidOperationException("The activity data root has changed.");
  }

  private SafeDirectoryHandle OpenRootUnanchored()
  {
    return SafeDirectoryHandle.OpenPath(_root, permitChildren: false, createMissing: true);
  }

  private static NativeLease? TryLock(SafeDirectoryHandle root, long offset, bool exclusive, FileIdentity? expected = null)
  {
    var stream = new FileStream(root.OpenOrCreateFile("activity.lock"), FileAccess.ReadWrite);
    try
    {
      var identity = NativeFileIdentity.Read(stream.SafeFileHandle);
      if (expected is { } prior && identity != prior)
        throw new InvalidOperationException("The activity lock file changed during acquisition.");
      if (!NativeLease.Lock(stream.SafeFileHandle, offset, exclusive)) return null;
      var lease = new NativeLease(stream, offset, identity);
      stream = null!;
      return lease;
    }
    finally
    {
      stream?.Dispose();
    }
  }

  private sealed class RootedLease(SafeDirectoryHandle root, IDisposable inner) : IDisposable
  {
    private bool _disposed;
    public void Dispose()
    {
      if (_disposed) return;
      _disposed = true;
      try { inner.Dispose(); }
      finally { root.Dispose(); }
    }
  }

  private sealed class CombinedLease(NativeLease gate, NativeLease jobs) : IDisposable
  {
    private bool _disposed;
    public void Dispose()
    {
      if (_disposed) return;
      _disposed = true;
      try { jobs.Dispose(); }
      finally { gate.Dispose(); }
    }
  }

  private sealed class NativeLease(FileStream stream, long offset, FileIdentity identity) : IDisposable
  {
    private bool _disposed;
    public bool Transferred { get; set; }
    public FileIdentity Identity => identity;

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

    public static bool Lock(SafeFileHandle handle, long offset, bool exclusive)
    {
      var overlapped = new Overlapped { Offset = checked((uint)offset) };
      if (LockFileEx(handle, 1u | (exclusive ? 2u : 0u), 0, 1, 0, ref overlapped)) return true;
      var error = Marshal.GetLastWin32Error();
      if (error is 33 or 158) return false;
      throw new Win32Exception(error, "The activity lock could not be acquired.");
    }

    public void Dispose()
    {
      if (_disposed) return;
      _disposed = true;
      var overlapped = new Overlapped { Offset = checked((uint)offset) };
      try
      {
        if (!UnlockFileEx(stream.SafeFileHandle, 0, 1, 0, ref overlapped))
          throw new Win32Exception(Marshal.GetLastWin32Error(), "The activity lock could not be released.");
      }
      finally { stream.Dispose(); }
    }
  }
}
