using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Updates.Windows;

public readonly record struct FileIdentity(ulong VolumeSerial, string FileIdHex)
{
  public bool IsValid => FileIdHex is { Length: 32 } &&
    FileIdHex.All(Uri.IsHexDigit) &&
    FileIdHex.Any(character => character != '0');
}

[SupportedOSPlatform("windows")]
internal static class NativeFileIdentity
{
  [StructLayout(LayoutKind.Sequential)]
  private struct FileIdInfo
  {
    public ulong VolumeSerialNumber;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] FileId;
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,
    int infoClass, out FileIdInfo info, uint bufferSize);

  internal static FileIdentity Read(SafeFileHandle handle)
  {
    if (!GetFileInformationByHandleEx(handle, 18, out var info, 24))
    {
      throw new Win32Exception(Marshal.GetLastWin32Error(), "The directory file ID is unavailable.");
    }

    var identity = new FileIdentity(info.VolumeSerialNumber, Convert.ToHexString(info.FileId));
    if (!identity.IsValid)
    {
      throw new InvalidOperationException("The filesystem returned an invalid directory file ID.");
    }

    return identity;
  }
}
