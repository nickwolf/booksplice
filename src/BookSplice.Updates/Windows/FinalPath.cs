using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Updates.Windows;

[SupportedOSPlatform("windows")]
public static class FinalPath
{
  private const uint FileReadAttributes = 0x80;
  private const uint ShareAll = 7;
  private const uint OpenExisting = 3;
  private const uint FileFlagBackupSemantics = 0x02000000;

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern SafeFileHandle CreateFileW(string path, uint desiredAccess, uint shareMode,
    nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] buffer,
    uint length, uint flags);

  // Follows junctions and symlinks in a directory path. Falls back to the given path when it cannot be resolved.
  public static string ResolveDirectory(string path)
  {
    var full = Path.GetFullPath(path);
    try
    {
      using var handle = CreateFileW(full, FileReadAttributes, ShareAll, 0, OpenExisting,
        FileFlagBackupSemantics, 0);
      if (handle.IsInvalid) return full;
      var buffer = new char[512];
      var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
      if (length >= buffer.Length)
      {
        buffer = new char[length + 1];
        length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
      }
      return length == 0 || length >= buffer.Length ? full : StripPrefix(new string(buffer, 0, (int)length));
    }
    catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
    {
      return full;
    }
  }

  internal static string StripPrefix(string path) =>
    path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..] :
    path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
}
