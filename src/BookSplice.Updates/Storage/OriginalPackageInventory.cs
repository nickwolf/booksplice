using System.Runtime.Versioning;
using BookSplice.Updates.Windows;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BookSplice.Updates.Storage;

[SupportedOSPlatform("windows")]
public sealed class OriginalPackageInventory : IDisposable
{
  [StructLayout(LayoutKind.Sequential)]
  private struct FileInformation
  {
    public uint Attributes;
    public uint CreatedLow;
    public uint CreatedHigh;
    public uint AccessedLow;
    public uint AccessedHigh;
    public uint WrittenLow;
    public uint WrittenHigh;
    public uint VolumeSerial;
    public uint SizeHigh;
    public uint SizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetFileInformationByHandle(SafeFileHandle handle,
    out FileInformation information);

  private static void RequireSingleLink(SafeFileHandle handle)
  {
    if (!GetFileInformationByHandle(handle, out var information))
      throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect package file links.");
    if (information.NumberOfLinks != 1)
      throw new PackageException("The original package contains a hard-linked file.");
  }

  private readonly SafeDirectoryHandle _root;
  private readonly Dictionary<string, SafeDirectoryHandle> _directories;
  private readonly Dictionary<string, SafeFileHandle> _files;
  private readonly PackageSnapshot _snapshot;
  private bool _disposed;

  private OriginalPackageInventory(SafeDirectoryHandle root,
    Dictionary<string, SafeDirectoryHandle> directories,
    Dictionary<string, SafeFileHandle> files, PackageSnapshot snapshot,
    FileIdentity parentIdentity)
  {
    _root = root;
    _directories = directories;
    _files = files;
    _snapshot = snapshot;
    ParentIdentity = parentIdentity;
  }

  public FileIdentity ParentIdentity { get; }
  public string RootPath => _root.Path;
  public PackageSnapshot Snapshot => _snapshot;

  public static string BindingDigest(FileIdentity parentIdentity, PackageSnapshot snapshot)
  {
    var canonical = $"{parentIdentity.VolumeSerial}\t{parentIdentity.FileIdHex}\n" +
      $"{snapshot.RootIdentity.VolumeSerial}\t{snapshot.RootIdentity.FileIdHex}\n" +
      snapshot.Sha256;
    return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
  }

  public static OriginalPackageInventory Capture(string path, FileIdentity expectedRoot,
    string expectedVersion)
  {
    ValidatePath(path);
    // Only the identity is kept: holding the parent would block renames in it (e.g. Downloads) during the scan.
    FileIdentity parentIdentity;
    using (var parent = SafeDirectoryHandle.OpenPath(Path.GetDirectoryName(path)!, false))
      parentIdentity = parent.Identity;
    var inventory = Open(path, parentIdentity, expectedRoot, null);
    try
    {
      inventory.ValidatePackage(expectedVersion);
      return inventory;
    }
    catch { inventory.Dispose(); throw; }
  }

  public static OriginalPackageInventory Reopen(string path, FileIdentity expectedParent,
    PersistedPackage persisted, string expectedVersion)
  {
    ValidatePath(path);
    ArgumentNullException.ThrowIfNull(persisted);
    var snapshot = persisted.Restore();
    var inventory = Open(path, expectedParent, snapshot.RootIdentity, snapshot);
    try
    {
      inventory.ValidatePackage(expectedVersion);
      return inventory;
    }
    catch { inventory.Dispose(); throw; }
  }

  private static OriginalPackageInventory Open(string path, FileIdentity expectedParent,
    FileIdentity expectedRoot, PackageSnapshot? expected)
  {
    var parentPath = Path.GetDirectoryName(path)!;
    SafeDirectoryHandle? root = null;
    var directories = new Dictionary<string, SafeDirectoryHandle>(StringComparer.OrdinalIgnoreCase);
    var files = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
    try
    {
      root = SafeDirectoryHandle.OpenPath(path, false, parentAnchorPath: parentPath,
        parentAnchorIdentity: expectedParent);
      if (root.Identity != expectedRoot)
        throw new PackageException("The original package root was replaced.");
      var recordedDirectories = new List<PackageDirectory>();
      var recordedFiles = new List<PackageFile>();
      var pending = new Queue<(string RelativePath, SafeDirectoryHandle Handle)>();
      pending.Enqueue(("", root));
      long total = 0;
      while (pending.TryDequeue(out var current))
      {
        foreach (var entry in Directory.EnumerateFileSystemEntries(current.Handle.Path))
        {
          if (recordedDirectories.Count + recordedFiles.Count >= 20_000)
            throw new PackageException("The original package has too many entries.");
          var name = Path.GetFileName(entry);
          var relative = current.RelativePath.Length == 0 ? name : current.RelativePath + "/" + name;
          if (entry.Length > 4096 || relative.Length > 4096 ||
              !SafeZipExtractor.IsSafeComponent(name) ||
              directories.ContainsKey(relative) || files.ContainsKey(relative))
            throw new PackageException("The original package has an invalid or colliding path.");
          var attributes = File.GetAttributes(entry);
          if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new PackageException("The original package contains a special entry.");
          if ((attributes & FileAttributes.Directory) != 0)
          {
            var child = SafeDirectoryHandle.OpenGuardedChild(current.Handle, name);
            directories.Add(relative, child);
            recordedDirectories.Add(new PackageDirectory(relative, child.Identity));
            pending.Enqueue((relative, child));
          }
          else
          {
            var handle = current.Handle.OpenFile(name);
            files.Add(relative, handle);
            SafeDirectoryHandle.RejectReparseFile(handle);
            RequireSingleLink(handle);
            var length = RandomAccess.GetLength(handle);
            if (length is < 0 or > 1024L * 1024 * 1024 ||
                (total = checked(total + length)) > 4L * 1024 * 1024 * 1024)
              throw new PackageException("The original package exceeds its file limits.");
            recordedFiles.Add(new PackageFile(relative, length,
              PackageInventory.HashHandle(handle), NativeFileIdentity.Read(handle)));
          }
        }
      }
      var snapshot = new PackageSnapshot(root.Identity,
        recordedDirectories.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray(),
        recordedFiles.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray());
      if (expected is not null &&
          !string.Equals(snapshot.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
        throw new PackageException("The original package differs from its recorded inventory.");
      var result = new OriginalPackageInventory(root, directories, files, snapshot, expectedParent);
      root = null;
      directories = new Dictionary<string, SafeDirectoryHandle>();
      files = new Dictionary<string, SafeFileHandle>();
      try { result.Revalidate(); return result; }
      catch { result.Dispose(); throw; }
    }
    finally
    {
      foreach (var handle in files.Values) handle.Dispose();
      foreach (var handle in directories.Values) handle.Dispose();
      root?.Dispose();
    }
  }

  private static void ValidatePath(string path)
  {
    ArgumentNullException.ThrowIfNull(path);
    var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    if (!Path.IsPathFullyQualified(path) ||
        !string.Equals(path, full, StringComparison.OrdinalIgnoreCase) ||
        path.Length > 4096)
      throw new PackageException("The original package path must be canonical and bounded.");
  }

  public void Revalidate()
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    using var reopened = SafeDirectoryHandle.OpenPath(_root.Path, false,
      parentAnchorPath: Path.GetDirectoryName(_root.Path), parentAnchorIdentity: ParentIdentity);
    if (reopened.Identity != _snapshot.RootIdentity)
      throw new PackageException("The original package root changed.");
    var expectedFiles = _snapshot.Files.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
    var expectedDirectories = _snapshot.Directories.ToDictionary(x => x.RelativePath,
      StringComparer.OrdinalIgnoreCase);
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var pair in _directories.Prepend(new KeyValuePair<string, SafeDirectoryHandle>("", _root)))
      foreach (var entry in Directory.EnumerateFileSystemEntries(pair.Value.Path))
      {
        if ((File.GetAttributes(entry) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
          throw new PackageException("The original package contains a special entry.");
        var name = Path.GetFileName(entry);
        var relative = pair.Key.Length == 0 ? name : pair.Key + "/" + name;
        if (!seen.Add(relative)) throw new PackageException("The original package path collides.");
        if (expectedDirectories.TryGetValue(relative, out var directory))
        {
          if (!string.Equals(relative, directory.RelativePath, StringComparison.Ordinal))
            throw new PackageException("The original package directory name changed.");
          using var actual = SafeDirectoryHandle.OpenBorrowedChild(pair.Value, name);
          if (actual.Identity != directory.Identity)
            throw new PackageException("The original package directory changed.");
        }
        else if (expectedFiles.TryGetValue(relative, out var file))
        {
          if (!string.Equals(relative, file.RelativePath, StringComparison.Ordinal))
            throw new PackageException("The original package file name changed.");
          using var actual = pair.Value.OpenFile(name);
          SafeDirectoryHandle.RejectReparseFile(actual);
          RequireSingleLink(actual);
          if (NativeFileIdentity.Read(actual) != file.Identity)
            throw new PackageException("The original package file changed.");
        }
        else throw new PackageException("The original package contains an unrecorded entry.");
      }
    if (seen.Count != expectedFiles.Count + expectedDirectories.Count)
      throw new PackageException("The original package is missing an entry.");
    foreach (var file in _snapshot.Files)
    {
      var handle = _files[file.RelativePath];
      RequireSingleLink(handle);
      if (NativeFileIdentity.Read(handle) != file.Identity ||
          RandomAccess.GetLength(handle) != file.Length ||
          !string.Equals(PackageInventory.HashHandle(handle), file.Sha256,
            StringComparison.OrdinalIgnoreCase))
        throw new PackageException("The original package file bytes changed.");
    }
  }

  public void ValidatePackage(string expectedVersion)
  {
    Revalidate();
    PackagePayloadValidator.Validate(_snapshot.Files, ReadText, expectedVersion);
  }

  private string ReadText(string path, long limit)
  {
    var file = _snapshot.Files.Single(x => string.Equals(x.RelativePath, path,
      StringComparison.OrdinalIgnoreCase));
    if (file.Length > limit) throw new PackageException("Package text file exceeds its limit.");
    var bytes = new byte[checked((int)file.Length)];
    var offset = 0;
    while (offset < bytes.Length)
    {
      var count = RandomAccess.Read(_files[file.RelativePath], bytes.AsSpan(offset), offset);
      if (count == 0) throw new PackageException("Package text file was truncated.");
      offset += count;
    }
    return new System.Text.UTF8Encoding(false, true).GetString(bytes);
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    foreach (var file in _files.Values) file.Dispose();
    foreach (var directory in _directories.Values) directory.Dispose();
    _root.Dispose();
  }
}
