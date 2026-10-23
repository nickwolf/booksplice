using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using BookSplice.Updates.Security;
using BookSplice.Updates.Windows;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Updates.Storage;

public sealed class PackageException(string message, Exception? inner = null) : IOException(message, inner);

public sealed record PackageFile(string RelativePath, long Length, string Sha256, FileIdentity Identity);

public sealed record PackageDirectory(string RelativePath, FileIdentity Identity);

public sealed class PackageSnapshot
{
  internal PackageSnapshot(FileIdentity rootIdentity, IReadOnlyList<PackageDirectory> directories,
    IReadOnlyList<PackageFile> files)
  {
    RootIdentity = rootIdentity;
    Directories = Array.AsReadOnly(directories.ToArray());
    Files = Array.AsReadOnly(files.ToArray());
    var canonical = string.Join("\n", Directories.Select(directory =>
        $"D\t{directory.RelativePath}\t{directory.Identity.VolumeSerial}\t{directory.Identity.FileIdHex}")
      .Concat(Files.Select(file =>
        $"F\t{file.RelativePath}\t{file.Length}\t{file.Sha256}\t{file.Identity.VolumeSerial}\t{file.Identity.FileIdHex}")));
    Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
  }

  public FileIdentity RootIdentity { get; }
  public IReadOnlyList<PackageDirectory> Directories { get; }
  public IReadOnlyList<PackageFile> Files { get; }
  public string Sha256 { get; }
}

[SupportedOSPlatform("windows")]
public sealed class PackageInventory : IDisposable
{
  private readonly SafeDirectoryHandle _root;
  private readonly IReadOnlyDictionary<string, SafeDirectoryHandle> _directories;
  private readonly IReadOnlyDictionary<string, SafeFileHandle> _handles;
  private readonly IReadOnlyDictionary<string, PackageFile> _files;
  private bool _disposed;

  internal PackageInventory(OwnedDirectory destination, SafeDirectoryHandle root,
    Dictionary<string, SafeDirectoryHandle> directories, Dictionary<string, SafeFileHandle> handles,
    IEnumerable<PackageFile> files)
  {
    Destination = destination;
    _root = root;
    _directories = new ReadOnlyDictionary<string, SafeDirectoryHandle>(directories);
    _handles = new ReadOnlyDictionary<string, SafeFileHandle>(handles);
    _files = new ReadOnlyDictionary<string, PackageFile>(files.ToDictionary(file => file.RelativePath,
      StringComparer.OrdinalIgnoreCase));
    Files = Array.AsReadOnly(_files.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray());
  }

  public OwnedDirectory Destination { get; }
  public IReadOnlyList<PackageFile> Files { get; }

  public PackageSnapshot Snapshot()
  {
    Revalidate();
    return new PackageSnapshot(Destination.Identity,
      _directories.Select(pair => new PackageDirectory(pair.Key, pair.Value.Identity))
        .OrderBy(directory => directory.RelativePath, StringComparer.Ordinal).ToArray(), Files);
  }

  internal static PackageInventory ReopenSnapshot(OwnedDirectory owned, PackageSnapshot snapshot,
    OwnedDirectoryStore store, bool permitRename, OwnedDirectory? expectedParent = null)
  {
    if (owned.Identity != snapshot.RootIdentity)
      throw new PackageException("The package root identity differs from the staged snapshot.");
    SafeDirectoryHandle? root = null;
    var directories = new Dictionary<string, SafeDirectoryHandle>(StringComparer.OrdinalIgnoreCase);
    var handles = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
    try
    {
      root = permitRename ? store.OpenValidatedForRename(owned,
        expectedParent ?? throw new PackageException("The promotion source parent is missing.")) :
        store.OpenValidated(owned);
      foreach (var directory in snapshot.Directories.OrderBy(directory =>
        directory.RelativePath.Count(character => character == '/')))
      {
        var parentPath = ParentPath(directory.RelativePath);
        var parent = parentPath.Length == 0 ? root : directories[parentPath];
        var handle = SafeDirectoryHandle.OpenGuardedChild(parent, LeafName(directory.RelativePath));
        directories.Add(directory.RelativePath, handle);
        if (handle.Identity != directory.Identity)
          throw new PackageException("A package directory was replaced.");
      }
      foreach (var file in snapshot.Files)
      {
        var parentPath = ParentPath(file.RelativePath);
        var parent = parentPath.Length == 0 ? root : directories[parentPath];
        var handle = parent.OpenFile(LeafName(file.RelativePath));
        handles.Add(file.RelativePath, handle);
        SafeDirectoryHandle.RejectReparseFile(handle);
        if (NativeFileIdentity.Read(handle) != file.Identity)
          throw new PackageException("A package file was replaced.");
      }
      var inventory = new PackageInventory(owned, root, directories, handles, snapshot.Files);
      root = null;
      directories = new Dictionary<string, SafeDirectoryHandle>();
      handles = new Dictionary<string, SafeFileHandle>();
      try { inventory.Revalidate(); return inventory; }
      catch { inventory.Dispose(); throw; }
    }
    finally
    {
      foreach (var handle in handles.Values) handle.Dispose();
      foreach (var directory in directories.Values) directory.Dispose();
      root?.Dispose();
    }
  }

  internal SafeDirectoryHandle RootHandle => _root;

  internal void ReleaseContentsForRename()
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    foreach (var handle in _handles.Values) handle.Dispose();
    foreach (var directory in _directories.Values.Reverse()) directory.Dispose();
  }

  private static string ParentPath(string path) => path.LastIndexOf('/') is var index && index >= 0 ?
    path[..index] : "";

  private static string LeafName(string path) => path[(path.LastIndexOf('/') + 1)..];

  public void Revalidate()
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (_root.Identity != Destination.Identity) throw new PackageException("The owned directory changed.");
    var foundFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var foundDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var pair in _directories.Prepend(new KeyValuePair<string, SafeDirectoryHandle>("", _root)))
    {
      foreach (var path in Directory.EnumerateFileSystemEntries(pair.Value.Path))
      {
        var name = Path.GetFileName(path);
        var relative = pair.Key.Length == 0 ? name : pair.Key + "/" + name;
        if (_directories.TryGetValue(relative, out var expectedDirectory))
        {
          using var actual = SafeDirectoryHandle.OpenBorrowedChild(pair.Value, name);
          if (actual.Identity != expectedDirectory.Identity || !foundDirectories.Add(relative))
            throw new PackageException("A package directory changed.");
        }
        else if (_files.TryGetValue(relative, out var expectedFile))
        {
          using var actual = pair.Value.OpenFile(name);
          SafeDirectoryHandle.RejectReparseFile(actual);
          if (NativeFileIdentity.Read(actual) != expectedFile.Identity || !foundFiles.Add(relative))
            throw new PackageException("A package file changed.");
        }
        else
        {
          throw new PackageException("The package contains an unrecorded entry.");
        }
      }
    }
    if (foundFiles.Count != _files.Count || foundDirectories.Count != _directories.Count)
      throw new PackageException("A package entry is missing.");
    foreach (var file in Files)
    {
      var handle = _handles[file.RelativePath];
      if (NativeFileIdentity.Read(handle) != file.Identity) throw new PackageException("A package file changed.");
      if (RandomAccess.GetLength(handle) != file.Length || !string.Equals(HashHandle(handle),
        file.Sha256, StringComparison.OrdinalIgnoreCase))
        throw new PackageException("A package file changed.");
    }
  }

  public void ValidatePackage(VerifiedManifest manifest)
  {
    ArgumentNullException.ThrowIfNull(manifest);
    Revalidate();
    PackagePayloadValidator.Validate(Files, ReadText, manifest.Version);
  }

  internal void ValidatePackage(string expectedVersion)
  {
    Revalidate();
    PackagePayloadValidator.Validate(Files, ReadText, expectedVersion);
  }

  private string ReadText(string path, long limit)
  {
    if (!_files.TryGetValue(path, out var file))
      throw new PackageException("Required package file is missing: " + path);
    if (file.Length > limit) throw new PackageException("Package text file exceeds its limit.");
    var bytes = new byte[checked((int)file.Length)];
    var offset = 0;
    while (offset < bytes.Length)
    {
      var count = RandomAccess.Read(_handles[path], bytes.AsSpan(offset), offset);
      if (count == 0) throw new PackageException("Package text file was truncated.");
      offset += count;
    }
    try { return new UTF8Encoding(false, true).GetString(bytes); }
    catch (DecoderFallbackException error) { throw new PackageException("Package text is not UTF-8.", error); }
  }

  internal static string HashHandle(SafeFileHandle handle)
  {
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var buffer = new byte[81920];
    long offset = 0;
    while (true)
    {
      var count = RandomAccess.Read(handle, buffer, offset);
      if (count == 0) break;
      hash.AppendData(buffer, 0, count);
      offset += count;
    }
    return Convert.ToHexString(hash.GetHashAndReset());
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    foreach (var handle in _handles.Values) handle.Dispose();
    foreach (var directory in _directories.Values.Reverse()) directory.Dispose();
    _root.Dispose();
  }
}
