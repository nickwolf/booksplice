using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookSplice.Updates.Policy;
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
    var required = new[]
    {
      "BookSplice.Gui.exe", "booksplice.exe", "BookSplice.Launcher.exe", "LICENSE", "README.md",
      "THIRD-PARTY-NOTICES.md", "VERSION.txt", "licenses/FFmpeg-LICENSE.txt",
      "licenses/FFmpeg-components.md", "licenses/LGPL-2.1.txt", "licenses/zlib-LICENSE.txt",
      "tools/ffmpeg/ffmpeg.exe", "tools/ffmpeg/ffprobe.exe", "sources/manifest.json",
      "sources/BUILDING.md", "sources/Dockerfile", "sources/build-minimal.sh",
      "sources/Build-MediaTools.ps1"
    };
    foreach (var path in required) Require(path);
    if (Files.Any(file => file.RelativePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
      throw new PackageException("The package contains debug symbols.");
    if (ReadText("README.md", UpdateLimits.ManifestMaxBytes).Contains("{{VERSION}}", StringComparison.Ordinal))
      throw new PackageException("The release README has an unresolved version placeholder.");

    var version = ReadText("VERSION.txt", 4096);
    var lines = version.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
    if (lines.Length != 4 || lines[0] != "BookSplice " + manifest.Version ||
        lines[1] != "Runtime: Windows x64, self-contained .NET 10" ||
        !lines[3].StartsWith("Media license SHA-256: ", StringComparison.Ordinal))
      throw new PackageException("VERSION.txt does not match the verified release.");
    var versionLicense = lines[3]["Media license SHA-256: ".Length..];
    if (!IsHash(versionLicense)) throw new PackageException("VERSION.txt has an invalid license hash.");

    using var json = ParseSourceManifest(ReadText("sources/manifest.json", UpdateLimits.ManifestMaxBytes));
    var root = json.RootElement;
    if (RequiredInt(root, "schemaVersion") != 2) throw new PackageException("Unsupported source manifest schema.");
    var mediaRelease = RequiredString(root, "release");
    if (mediaRelease.Length is < 1 or > 128 || mediaRelease.Any(character =>
        !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_')) ||
        lines[2] != "Media tools: source-built FFmpeg " + mediaRelease + ", LGPL 2.1 or later; zlib 1.3.2")
      throw new PackageException("VERSION.txt media release does not match the source manifest.");
    var license = RequiredHash(root, "licenseSha256");
    RequireHash("licenses/FFmpeg-LICENSE.txt", license);
    if (!string.Equals(versionLicense, license, StringComparison.OrdinalIgnoreCase))
      throw new PackageException("VERSION.txt and source manifest license hashes differ.");
    RequireHash("licenses/zlib-LICENSE.txt", RequiredHash(root, "zlibLicenseSha256"));
    RequireHash("tools/ffmpeg/ffmpeg.exe", RequiredHash(root, "ffmpegSha256"));
    RequireHash("tools/ffmpeg/ffprobe.exe", RequiredHash(root, "ffprobeSha256"));
    var sources = Required(root, "sourceArchives");
    if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() is < 1 or > 100)
      throw new PackageException("Invalid source archive list.");
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
      "manifest.json", "BUILDING.md", "Dockerfile", "build-minimal.sh", "Build-MediaTools.ps1"
    };
    foreach (var source in sources.EnumerateArray())
    {
      if (source.ValueKind != JsonValueKind.Object) throw new PackageException("Invalid source archive entry.");
      var name = RequiredString(source, "fileName");
      if (!SafeZipExtractor.IsSafeComponent(name) || !names.Add(name))
        throw new PackageException("Invalid or duplicate source archive name.");
      RequireHash("sources/" + name, RequiredHash(source, "sha256"));
    }
  }

  private void RequireHash(string path, string hash)
  {
    if (!string.Equals(Require(path).Sha256, hash, StringComparison.OrdinalIgnoreCase))
      throw new PackageException("Package checksum mismatch: " + path);
  }

  private PackageFile Require(string path) => _files.TryGetValue(path, out var file) ? file :
    throw new PackageException("Required package file is missing: " + path);

  private string ReadText(string path, long limit)
  {
    var file = Require(path);
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

  private static JsonDocument ParseSourceManifest(string text)
  {
    try
    {
      var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
      CheckDuplicateProperties(document.RootElement);
      if (document.RootElement.ValueKind != JsonValueKind.Object) throw new PackageException("Invalid source manifest.");
      return document;
    }
    catch (JsonException error) { throw new PackageException("Invalid source manifest JSON.", error); }
  }

  private static void CheckDuplicateProperties(JsonElement element)
  {
    if (element.ValueKind == JsonValueKind.Object)
    {
      var names = new HashSet<string>(StringComparer.Ordinal);
      foreach (var property in element.EnumerateObject())
      {
        if (!names.Add(property.Name)) throw new PackageException("Duplicate source manifest property.");
        CheckDuplicateProperties(property.Value);
      }
    }
    else if (element.ValueKind == JsonValueKind.Array)
      foreach (var item in element.EnumerateArray()) CheckDuplicateProperties(item);
  }

  private static JsonElement Required(JsonElement parent, string name) =>
    parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value :
    throw new PackageException("Missing source manifest property: " + name);

  private static string RequiredString(JsonElement parent, string name)
  {
    var value = Required(parent, name);
    return value.ValueKind == JsonValueKind.String && value.GetString() is { } text ? text :
      throw new PackageException("Invalid source manifest string: " + name);
  }

  private static int RequiredInt(JsonElement parent, string name)
  {
    var value = Required(parent, name);
    return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number :
      throw new PackageException("Invalid source manifest number: " + name);
  }

  private static string RequiredHash(JsonElement parent, string name)
  {
    var value = RequiredString(parent, name);
    return IsHash(value) ? value : throw new PackageException("Invalid source manifest hash: " + name);
  }

  private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

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
