using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Core.Discovery;

public sealed record SourceIdentity(string Path, long Length, DateTime LastWriteUtc, string? PhysicalId)
{
  public bool Matches(SourceIdentity other)
    => string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) && Length == other.Length && LastWriteUtc == other.LastWriteUtc && string.Equals(PhysicalId, other.PhysicalId, StringComparison.OrdinalIgnoreCase);

  public static SourceIdentity Capture(string path)
  {
    var full = System.IO.Path.GetFullPath(path);
    using var stream = Open(full);
    return new SourceIdentity(full, stream.Length, File.GetLastWriteTimeUtc(full), PhysicalIdOf(stream));
  }

  // Platforms without a stable file identity retain the normalized lexical path fallback.
  internal static string? TryGetPhysicalId(string path)
  {
    try
    {
      using var stream = Open(path);
      return PhysicalIdOf(stream);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
  }

  private static FileStream Open(string path) => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

  private static string? PhysicalIdOf(FileStream stream)
  {
    if (!OperatingSystem.IsWindows()) return null;
    if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information)) return null;
    return $"{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";
  }

  [SuppressMessage("Interoperability", "SYSLIB1054:Use 'LibraryImportAttribute' instead of 'DllImportAttribute' to generate P/Invoke marshalling code at compile time")]
  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

  [StructLayout(LayoutKind.Sequential)]
  private struct ByHandleFileInformation
  {
    public uint FileAttributes;
    public uint CreationTimeLow;
    public uint CreationTimeHigh;
    public uint LastAccessTimeLow;
    public uint LastAccessTimeHigh;
    public uint LastWriteTimeLow;
    public uint LastWriteTimeHigh;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
  }
}
