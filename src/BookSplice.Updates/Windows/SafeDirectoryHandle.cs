using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Updates.Windows;

[SupportedOSPlatform("windows")]
public sealed class SafeDirectoryHandle : IDisposable
{
  private const uint FileReadAttributes = 0x80;
  private const uint FileTraverse = 0x20;
  private const uint FileAddSubdirectory = 0x4;
  private const uint FileReadData = 0x1;
  private const uint FileWriteData = 0x2;
  private const uint Delete = 0x10000;
  private const uint FileAttributeReparsePoint = 0x400;
  private const uint FileFlagBackupSemantics = 0x02000000;
  private const uint FileFlagOpenReparsePoint = 0x00200000;
  private const uint FileDirectoryFile = 0x1;
  private const uint FileNonDirectoryFile = 0x40;
  private const uint FileOpenReparsePoint = 0x00200000;
  private const uint FileSynchronousIoNonAlert = 0x20;
  private const uint Synchronize = 0x100000;
  private const uint FileCreate = 2;
  private const uint FileOpen = 1;
  private const uint FileOpenIf = 3;
  private const uint FileWriteThrough = 0x2;
  private const uint ObjCaseInsensitive = 0x40;
  private readonly List<SafeFileHandle> _ancestry;
  private readonly SafeDirectoryHandle? _parent;
  private bool _disposed;

  public string Path { get; }
  public FileIdentity Identity { get; }
  public SafeFileHandle Handle { get; }

  public static SafeDirectoryHandle OpenCanonical(string path) => OpenPath(path, false);

  internal SafeDirectoryHandle(string path, SafeFileHandle handle, List<SafeFileHandle> ancestry,
    SafeDirectoryHandle? parent = null)
  {
    Path = path;
    Handle = handle;
    _ancestry = ancestry;
    _parent = parent;
    RejectReparse(handle);
    Identity = NativeFileIdentity.Read(handle);
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern SafeFileHandle CreateFileW(string path, uint desiredAccess, uint shareMode,
    nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern uint GetDriveTypeW([MarshalAs(UnmanagedType.LPWStr)] string root);

  [DllImport("ntdll.dll")]
  private static extern int NtCreateFile(out nint handle, uint desiredAccess, ref ObjectAttributes attributes,
    out IoStatusBlock statusBlock, nint allocationSize, uint fileAttributes, uint shareAccess,
    uint createDisposition, uint createOptions, nint eaBuffer, uint eaLength);

  [DllImport("ntdll.dll")]
  private static extern uint RtlNtStatusToDosError(int status);

  [DllImport("ntdll.dll")]
  private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock statusBlock,
    nint information, uint size, int infoClass);

  [StructLayout(LayoutKind.Sequential)]
  private struct UnicodeString
  {
    public ushort Length;
    public ushort MaximumLength;
    public nint Buffer;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct ObjectAttributes
  {
    public uint Length;
    public nint RootDirectory;
    public nint ObjectName;
    public uint Attributes;
    public nint SecurityDescriptor;
    public nint SecurityQualityOfService;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct IoStatusBlock
  {
    public nint Status;
    public nint Information;
  }

  internal static SafeDirectoryHandle OpenPath(string path, bool permitChildren,
    string? anchorPath = null, FileIdentity? anchorIdentity = null, bool permitRename = false,
    bool destinationForRename = false, string? parentAnchorPath = null,
    FileIdentity? parentAnchorIdentity = null, bool createMissing = false,
    bool allowEntryMutation = false)
  {
    var full = System.IO.Path.GetFullPath(path);
    var root = System.IO.Path.GetPathRoot(full)!;
    if (root.Length != 3 || root[1] != ':' || GetDriveTypeW(root) != 3)
    {
      throw new NotSupportedException("Ownership requires a fixed local drive.");
    }

    var handles = new List<SafeFileHandle>();
    try
    {
      var current = CreateFileW(root, FileReadAttributes | FileTraverse, 1,
        0, 3, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
      if (current.IsInvalid)
      {
        var error = Marshal.GetLastWin32Error();
        current.Dispose();
        throw new Win32Exception(error, "Cannot open the volume root.");
      }

      handles.Add(current);
      RejectReparse(current);
      if (string.Equals(System.IO.Path.TrimEndingDirectorySeparator(root), anchorPath,
          StringComparison.OrdinalIgnoreCase) && NativeFileIdentity.Read(current) != anchorIdentity)
      {
        throw new InvalidOperationException("The allowed root has changed.");
      }
      var components = full[root.Length..].Split(System.IO.Path.DirectorySeparatorChar,
        StringSplitOptions.RemoveEmptyEntries);
      var walkedPath = root;
      for (var index = 0; index < components.Length; index++)
      {
        var access = FileReadAttributes | FileTraverse |
          (permitChildren && index == components.Length - 1 ? FileAddSubdirectory : 0) |
          (permitRename && index == components.Length - 1 ? Delete : 0);
        walkedPath = System.IO.Path.Combine(walkedPath, components[index]);
        current = createMissing ?
          OpenChild(handles[^1], components[index], access, FileOpenIf, FileDirectoryFile) :
          destinationForRename && index == components.Length - 1 ?
          OpenChild(handles[^1], components[index], access, FileOpen, FileDirectoryFile, 3) :
          CreateFileW(walkedPath, access,
            allowEntryMutation && index == components.Length - 1 ? 3u : 1u, 0, 3,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (current.IsInvalid)
        {
          var error = Marshal.GetLastWin32Error();
          current.Dispose();
          throw new Win32Exception(error, $"Cannot open directory component {components[index]}.");
        }
        handles.Add(current);
        RejectReparse(current);
        if (string.Equals(walkedPath, anchorPath, StringComparison.OrdinalIgnoreCase) &&
            NativeFileIdentity.Read(current) != anchorIdentity)
        {
          throw new InvalidOperationException("The allowed root has changed.");
        }
        if (string.Equals(walkedPath, parentAnchorPath, StringComparison.OrdinalIgnoreCase) &&
            NativeFileIdentity.Read(current) != parentAnchorIdentity)
        {
          throw new InvalidOperationException("The recorded parent has changed.");
        }
      }

      var leaf = handles[^1];
      handles.RemoveAt(handles.Count - 1);
      try
      {
        return new SafeDirectoryHandle(full, leaf, handles);
      }
      catch
      {
        leaf.Dispose();
        throw;
      }
    }
    catch
    {
      foreach (var handle in handles)
      {
        handle.Dispose();
      }

      throw;
    }
  }

  internal static SafeDirectoryHandle CreateChild(SafeDirectoryHandle parent, string name)
  {
    var handle = OpenChild(parent.Handle, name, FileReadAttributes | FileTraverse |
      FileAddSubdirectory, FileCreate);
    try
    {
      return new SafeDirectoryHandle(System.IO.Path.Combine(parent.Path, name), handle,
        new List<SafeFileHandle>(), parent);
    }
    catch
    {
      handle.Dispose();
      throw;
    }
  }

  internal static SafeDirectoryHandle CreateBorrowedChild(SafeDirectoryHandle parent, string name)
  {
    var handle = OpenChild(parent.Handle, name, FileReadAttributes | FileTraverse | FileAddSubdirectory,
      FileCreate);
    try
    {
      return new SafeDirectoryHandle(System.IO.Path.Combine(parent.Path, name), handle,
        new List<SafeFileHandle>());
    }
    catch
    {
      handle.Dispose();
      throw;
    }
  }

  internal static SafeDirectoryHandle OpenBorrowedChild(SafeDirectoryHandle parent, string name)
  {
    var handle = OpenChild(parent.Handle, name, FileReadAttributes | FileTraverse,
      FileOpen, FileDirectoryFile, 3);
    try
    {
      return new SafeDirectoryHandle(System.IO.Path.Combine(parent.Path, name), handle,
        new List<SafeFileHandle>());
    }
    catch
    {
      handle.Dispose();
      throw;
    }
  }

  internal static SafeDirectoryHandle OpenGuardedChild(SafeDirectoryHandle parent, string name)
  {
    var handle = OpenChild(parent.Handle, name, FileReadAttributes | FileTraverse,
      FileOpen, FileDirectoryFile);
    try
    {
      return new SafeDirectoryHandle(System.IO.Path.Combine(parent.Path, name), handle,
        new List<SafeFileHandle>());
    }
    catch
    {
      handle.Dispose();
      throw;
    }
  }

  internal void RenameChildTo(SafeDirectoryHandle destinationParent, string leafName)
  {
    if (!BookSplice.Updates.Storage.SafeZipExtractor.IsSafeComponent(leafName))
      throw new ArgumentException("Destination leaf name is invalid.", nameof(leafName));
    if (Identity.VolumeSerial != destinationParent.Identity.VolumeSerial)
      throw new IOException("Cross-volume promotion is not supported.");
    var filename = System.Text.Encoding.Unicode.GetBytes(leafName);
    var size = checked(24 + filename.Length);
    var buffer = Marshal.AllocHGlobal(size);
    try
    {
      Marshal.Copy(new byte[size], 0, buffer, size);
      Marshal.WriteIntPtr(buffer, 8, destinationParent.Handle.DangerousGetHandle());
      Marshal.WriteInt32(buffer, 16, filename.Length);
      Marshal.Copy(filename, 0, buffer + 20, filename.Length);
      var status = NtSetInformationFile(Handle, out _, buffer, (uint)size, 10);
      if (status < 0)
      {
        var error = (int)RtlNtStatusToDosError(status);
        throw new Win32Exception(error, $"The destination was occupied or promotion failed ({error}, NT 0x{status:X8}).");
      }
    }
    finally { Marshal.FreeHGlobal(buffer); }
  }

  // Deletes exactly this directory when the handle closes; it fails unless the directory is empty.
  internal void MarkForDeletion()
  {
    var buffer = Marshal.AllocHGlobal(1);
    try
    {
      Marshal.WriteByte(buffer, 1);
      var status = NtSetInformationFile(Handle, out _, buffer, 1, 13);
      if (status < 0)
      {
        var error = (int)RtlNtStatusToDosError(status);
        throw new Win32Exception(error, $"The directory could not be marked for deletion ({error}, NT 0x{status:X8}).");
      }
    }
    finally { Marshal.FreeHGlobal(buffer); }
  }

  internal SafeFileHandle CreateNewFile(string name) => OpenChild(Handle, name,
    FileReadData | FileWriteData | FileReadAttributes, FileCreate, FileNonDirectoryFile);

  internal SafeFileHandle CreateStateTemp(string name) => OpenChild(Handle, name,
    FileReadData | FileWriteData | FileReadAttributes | Delete | Synchronize,
    FileCreate, FileNonDirectoryFile, 1, synchronous: true, writeThrough: true);

  internal SafeFileHandle OpenStateFile(string name) => OpenChild(Handle, name,
    FileReadData | FileReadAttributes, FileOpen, FileNonDirectoryFile, 5);

  internal static void RenameStateTemp(SafeFileHandle temp, SafeDirectoryHandle parent,
    string name)
  {
    if (!BookSplice.Updates.Storage.SafeZipExtractor.IsSafeComponent(name))
      throw new ArgumentException("State filename is invalid.", nameof(name));
    var filename = System.Text.Encoding.Unicode.GetBytes(name);
    var size = checked(24 + filename.Length);
    var buffer = Marshal.AllocHGlobal(size);
    try
    {
      Marshal.Copy(new byte[size], 0, buffer, size);
      Marshal.WriteByte(buffer, 0, 1);
      Marshal.WriteIntPtr(buffer, 8, parent.Handle.DangerousGetHandle());
      Marshal.WriteInt32(buffer, 16, filename.Length);
      Marshal.Copy(filename, 0, buffer + 20, filename.Length);
      var status = NtSetInformationFile(temp, out _, buffer, (uint)size, 10);
      if (status < 0)
      {
        var error = (int)RtlNtStatusToDosError(status);
        throw new Win32Exception(error, $"Atomic state replacement failed ({error}, NT 0x{status:X8}).");
      }
    }
    finally { Marshal.FreeHGlobal(buffer); }
  }

  internal SafeFileHandle OpenFile(string name) => OpenChild(Handle, name,
    FileReadData | FileReadAttributes, FileOpen, FileNonDirectoryFile);

  internal SafeFileHandle OpenBridgeFile(string name) => OpenChild(Handle, name,
    FileReadData | FileReadAttributes, FileOpen, FileNonDirectoryFile, 3);

  internal SafeFileHandle OpenOrCreateFile(string name)
  {
    var handle = OpenChild(Handle, name, FileReadData | FileWriteData | FileReadAttributes | Synchronize,
      FileOpenIf, FileNonDirectoryFile, 3, synchronous: true);
    try
    {
      RejectReparseFile(handle);
      return handle;
    }
    catch
    {
      handle.Dispose();
      throw;
    }
  }

  private static SafeFileHandle OpenChild(SafeFileHandle parent, string name,
    uint access, uint disposition, uint type = FileDirectoryFile, uint share = 1,
    bool synchronous = false, bool writeThrough = false)
  {
    if (name.Length == 0 || name.Contains('\\') || name.Contains('/') || name is "." or "..")
    {
      throw new ArgumentException("Directory name must be one component.", nameof(name));
    }

    var stringBuffer = Marshal.StringToHGlobalUni(name);
    var unicode = new UnicodeString
    {
      Length = checked((ushort)(name.Length * 2)),
      MaximumLength = checked((ushort)((name.Length + 1) * 2)),
      Buffer = stringBuffer
    };
    var unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
    try
    {
      Marshal.StructureToPtr(unicode, unicodeBuffer, false);
      var attributes = new ObjectAttributes
      {
        Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
        RootDirectory = parent.DangerousGetHandle(),
        ObjectName = unicodeBuffer,
        Attributes = ObjCaseInsensitive
      };
      var status = NtCreateFile(out var rawHandle, access, ref attributes, out _, 0, 0,
        share, disposition, type | FileOpenReparsePoint |
        (synchronous ? FileSynchronousIoNonAlert : 0) |
        (writeThrough ? FileWriteThrough : 0), 0, 0);
      if (status < 0)
      {
        var error = (int)RtlNtStatusToDosError(status);
        if (disposition == FileCreate && (error == 80 || error == 183))
        {
          throw new IOException("The directory already exists.", new Win32Exception(error));
        }

        throw new Win32Exception(error, $"Cannot open directory component {name} (error {error}).");
      }

      return new SafeFileHandle(rawHandle, true);
    }
    finally
    {
      Marshal.FreeHGlobal(unicodeBuffer);
      Marshal.FreeHGlobal(stringBuffer);
    }
  }

  private static void RejectReparse(SafeFileHandle handle)
  {
    // Query by handle so a path replacement cannot turn this check into a different object.
    if (!GetFileInformationByHandleEx(handle, 0, out FileBasicInfo info, (uint)Marshal.SizeOf<FileBasicInfo>()))
    {
      throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect directory attributes.");
    }

    if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
    {
      throw new NotSupportedException("Reparse-point directories are not allowed.");
    }
  }

  internal static void RejectReparseFile(SafeFileHandle handle) => RejectReparse(handle);

  [StructLayout(LayoutKind.Sequential)]
  private struct FileBasicInfo
  {
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public uint FileAttributes;
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
    out FileBasicInfo info, uint size);

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    Handle.Dispose();
    foreach (var handle in _ancestry) handle.Dispose();
    _parent?.Dispose();
  }
}
