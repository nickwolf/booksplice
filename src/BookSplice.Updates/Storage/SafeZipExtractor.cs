using System.IO.Compression;
using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Windows;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Updates.Storage;

[SupportedOSPlatform("windows")]
public sealed class SafeZipExtractor
{
  private static readonly uint[] CrcTable = BuildCrcTable();
  private readonly OwnedDirectoryStore _store;
  private readonly int _entryLimit;
  private readonly long _fileLimit;
  private readonly long _totalLimit;
  private readonly Action<string>? _beforeInventoryHandoff;
  private readonly Action<string>? _beforePayloadWrites;
  private readonly Action<string>? _duringBridge;

  public SafeZipExtractor(OwnedDirectoryStore store) : this(store, UpdateLimits.MaxArchiveEntries,
    UpdateLimits.MaxExtractedFileBytes, UpdateLimits.MaxExtractedTotalBytes)
  { }

  internal SafeZipExtractor(OwnedDirectoryStore store, int entryLimit, long fileLimit, long totalLimit)
    : this(store, entryLimit, fileLimit, totalLimit, null) { }

  internal SafeZipExtractor(OwnedDirectoryStore store, int entryLimit, long fileLimit, long totalLimit,
    Action<string>? beforeInventoryHandoff, Action<string>? beforePayloadWrites = null,
    Action<string>? duringBridge = null)
  {
    _store = store ?? throw new ArgumentNullException(nameof(store));
    if (entryLimit < 1 || fileLimit < 0 || totalLimit < 0)
      throw new ArgumentOutOfRangeException(nameof(entryLimit));
    _entryLimit = entryLimit;
    _fileLimit = fileLimit;
    _totalLimit = totalLimit;
    _beforeInventoryHandoff = beforeInventoryHandoff;
    _beforePayloadWrites = beforePayloadWrites;
    _duringBridge = duringBridge;
  }

  public async Task<PackageInventory> ExtractAsync(Stream archive, OwnedDirectory destination,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(destination);
    cancellationToken.ThrowIfCancellationRequested();
    if (!archive.CanRead || !archive.CanSeek)
      throw new PackageException("The update ZIP must be a readable, seekable verified stream.");
    if (archive.Length > UpdateLimits.ZipMaxBytes)
      throw new PackageException("The update ZIP exceeds the archive limit.");
    try { CheckArchiveMetadata(archive); }
    catch (Exception error) when (error is IOException and not PackageException or
      NotSupportedException or ArgumentException)
    { throw new PackageException("The ZIP end record could not be read.", error); }
    SafeDirectoryHandle? root = null;
    var directories = new Dictionary<string, SafeDirectoryHandle>(StringComparer.OrdinalIgnoreCase);
    var handles = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
    try
    {
      root = _store.OpenValidated(destination);
      if (Directory.EnumerateFileSystemEntries(root.Path).Any())
        throw new PackageException("The owned destination must be empty.");
      using var zip = new ZipArchive(archive, ZipArchiveMode.Read, true);
      var plan = Preflight(zip, root.Path.Length, cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      if (plan.Files.Sum(file => file.Entry.Length) >
          new DriveInfo(Path.GetPathRoot(root.Path)!).AvailableFreeSpace)
        throw new PackageException("There is insufficient free space for the update package.");
      foreach (var directory in plan.Directories.OrderBy(path => path.Count(character => character == '/'))
        .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
      {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = Parent(directory);
        directories.Add(directory, SafeDirectoryHandle.CreateBorrowedChild(
          parent.Length == 0 ? root : directories[parent], Leaf(directory)));
      }
      _beforePayloadWrites?.Invoke(root.Path);
      var inventory = new List<PackageFile>();
      long total = 0;
      foreach (var (entry, path) in plan.Files)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = Parent(path);
        var directory = parent.Length == 0 ? root : directories[parent];
        FileIdentity writtenIdentity;
        string streamedHash;
        SafeFileHandle bridge;
        using (var target = directory.CreateNewFile(Leaf(path)))
        {
          writtenIdentity = NativeFileIdentity.Read(target);
          using var input = entry.Open();
          using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
          uint crc = uint.MaxValue;
          var buffer = new byte[81920];
          long length = 0;
          while (true)
          {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (length > _fileLimit - count || total > _totalLimit - count)
              throw new PackageException("The extracted content exceeds its limit.");
            hash.AppendData(buffer, 0, count);
            for (var index = 0; index < count; index++)
              crc = CrcTable[(crc ^ buffer[index]) & 0xff] ^ (crc >> 8);
            await RandomAccess.WriteAsync(target, buffer.AsMemory(0, count), length, cancellationToken)
              .ConfigureAwait(false);
            length += count;
            total += count;
          }
          RandomAccess.FlushToDisk(target);
          if (~crc != entry.Crc32)
            throw new PackageException("The ZIP entry checksum is invalid.");
          streamedHash = Convert.ToHexString(hash.GetHashAndReset());
          _beforeInventoryHandoff?.Invoke(Path.Combine(directory.Path, Leaf(path)));
          bridge = directory.OpenBridgeFile(Leaf(path));
          try
          {
            SafeDirectoryHandle.RejectReparseFile(bridge);
            if (NativeFileIdentity.Read(bridge) != writtenIdentity)
              throw new PackageException("The new package file changed identity.");
          }
          catch { bridge.Dispose(); throw; }
        }
        SafeFileHandle retained;
        using (bridge)
        {
          _duringBridge?.Invoke(Path.Combine(directory.Path, Leaf(path)));
          retained = directory.OpenFile(Leaf(path));
        }
        try
        {
          SafeDirectoryHandle.RejectReparseFile(retained);
          var length = RandomAccess.GetLength(retained);
          var finalHash = PackageInventory.HashHandle(retained);
          if (length != entry.Length || NativeFileIdentity.Read(retained) != writtenIdentity ||
              !string.Equals(finalHash, streamedHash, StringComparison.OrdinalIgnoreCase))
            throw new PackageException("The new package file changed after writing.");
          var file = new PackageFile(path, length, finalHash, writtenIdentity);
          handles.Add(path, retained);
          inventory.Add(file);
        }
        catch { retained.Dispose(); throw; }
      }
      cancellationToken.ThrowIfCancellationRequested();
      var result = new PackageInventory(destination, root, directories, handles, inventory);
      root = null;
      return result;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
    catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or
      System.ComponentModel.Win32Exception or NotSupportedException or OverflowException or
      ArgumentException or InvalidOperationException)
    {
      throw error is PackageException ? error : new PackageException("Update ZIP extraction failed.", error);
    }
    finally
    {
      if (root is not null)
      {
        foreach (var handle in handles.Values) handle.Dispose();
        foreach (var directory in directories.Values.Reverse()) directory.Dispose();
        root.Dispose();
      }
    }
  }

  private (List<(ZipArchiveEntry Entry, string Path)> Files, HashSet<string> Directories) Preflight(
    ZipArchive zip, int destinationPathLength, CancellationToken cancellationToken)
  {
    if (zip.Entries.Count > _entryLimit) throw new PackageException("The ZIP has too many entries.");
    var kinds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    var explicitEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var files = new List<(ZipArchiveEntry, string)>();
    var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    long total = 0;
    foreach (var entry in zip.Entries)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var raw = entry.FullName;
      var slashPath = raw.Replace('\\', '/');
      var isDirectory = slashPath.EndsWith('/');
      var normalized = isDirectory ? slashPath[..^1] : slashPath;
      var components = normalized.Split('/');
      if (raw.Length == 0 || normalized.StartsWith('/') || normalized.EndsWith('/') ||
          destinationPathLength + 1 + normalized.Length > 4096 || components.Length > _entryLimit ||
          components.Any(part => !IsSafeComponent(part)))
        throw new PackageException("The ZIP contains an unsafe path.");
      var mode = (entry.ExternalAttributes >> 16) & 0xF000;
      var unixAttributes = (entry.ExternalAttributes >> 16) & 0xffff;
      var dosAttributes = entry.ExternalAttributes & 0xffff;
      if (mode != 0 && mode != (isDirectory ? 0x4000 : 0x8000) ||
          (unixAttributes & 0x400) != 0 || (dosAttributes & 0x400) != 0 ||
          (!isDirectory && (dosAttributes & 0x10) != 0))
        throw new PackageException("The ZIP contains a link or special entry.");
      if (isDirectory && (entry.Length != 0 || entry.CompressedLength != 0))
        throw new PackageException("A ZIP directory contains payload bytes.");
      if (!isDirectory)
      {
        if (entry.Length > _fileLimit || total > _totalLimit - entry.Length)
          throw new PackageException("The ZIP declares too much output.");
        total += entry.Length;
      }
      for (var index = 1; index < components.Length; index++)
      {
        var parent = string.Join('/', components.Take(index));
        if (kinds.TryGetValue(parent, out var existing) && !existing)
          throw new PackageException("The ZIP has a file and directory collision.");
        kinds[parent] = true;
        if (kinds.Count > _entryLimit) throw new PackageException("The ZIP plans too many paths.");
        directories.Add(parent);
      }
      if (!explicitEntries.Add(normalized) || kinds.TryGetValue(normalized, out var priorKind) && (!priorKind || !isDirectory))
        throw new PackageException("The ZIP has duplicate or colliding paths.");
      kinds[normalized] = isDirectory;
      if (kinds.Count > _entryLimit) throw new PackageException("The ZIP plans too many paths.");
      if (isDirectory) directories.Add(normalized);
      else files.Add((entry, normalized));
    }
    return (files, directories);
  }

  internal static bool IsSafeComponent(string name)
  {
    if (name.Length is < 1 or > 255 || name is "." or ".." ||
        name.EndsWith('.') || name.EndsWith(' ') ||
        name.Any(character => character < 32 || character > 126 || character == '~' ||
          "<>:\"/\\|?*".Contains(character))) return false;
    var stem = name.Split('.')[0].ToUpperInvariant().Replace('¹', '1').Replace('²', '2').Replace('³', '3');
    if (stem is "CON" or "PRN" or "AUX" or "NUL") return false;
    if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
        stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9') return false;
    return true;
  }

  private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
  private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

  internal void CheckArchiveMetadata(Stream archive)
  {
    const int eocdLength = 22;
    var tailLength = (int)Math.Min(archive.Length, eocdLength + ushort.MaxValue);
    var tail = new byte[tailLength];
    archive.Position = archive.Length - tailLength;
    archive.ReadExactly(tail);
    for (var offset = tailLength - eocdLength; offset >= 0; offset--)
    {
      if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset)) != 0x06054b50) continue;
      var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 20));
      if (offset + eocdLength + commentLength != tailLength)
        throw new PackageException("The ZIP end record is ambiguous.");
      var disk = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 4));
      var centralDisk = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 6));
      var diskCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 8));
      var totalCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 10));
      var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset + 12));
      var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset + 16));
      if (disk != 0 || centralDisk != 0 || diskCount != totalCount || totalCount == ushort.MaxValue ||
          centralSize == uint.MaxValue || centralOffset == uint.MaxValue || centralSize > 64L * 1024 * 1024 ||
          totalCount > _entryLimit ||
          (long)centralOffset + centralSize > archive.Length - tailLength + offset)
        throw new PackageException("The ZIP central directory exceeds its supported bounds.");
      CheckEntryHeaders(archive, centralOffset, centralSize, totalCount);
      archive.Position = 0;
      return;
    }
    throw new PackageException("The ZIP end record is missing or malformed.");
  }

  private static void CheckEntryHeaders(Stream archive, uint centralOffset, uint centralSize, ushort count)
  {
    var centralEnd = (long)centralOffset + centralSize;
    long next = centralOffset;
    Span<byte> centralHeader = stackalloc byte[46];
    Span<byte> localHeader = stackalloc byte[30];
    for (var index = 0; index < count; index++)
    {
      if (next > centralEnd - centralHeader.Length)
        throw new PackageException("A ZIP central entry exceeds its directory.");
      archive.Position = next;
      archive.ReadExactly(centralHeader);
      if (BinaryPrimitives.ReadUInt32LittleEndian(centralHeader) != 0x02014b50)
        throw new PackageException("A ZIP central entry is malformed.");
      var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(centralHeader[28..]);
      var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(centralHeader[30..]);
      var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(centralHeader[32..]);
      if (extraLength != 0) throw new PackageException("ZIP extra fields are unsupported.");
      next += centralHeader.Length + nameLength + extraLength + commentLength;
      if (next > centralEnd) throw new PackageException("A ZIP central entry exceeds its directory.");
      var centralName = new byte[nameLength];
      archive.ReadExactly(centralName);

      var localOffset = BinaryPrimitives.ReadUInt32LittleEndian(centralHeader[42..]);
      if ((long)localOffset > (long)centralOffset - localHeader.Length)
        throw new PackageException("A ZIP local entry is outside the payload.");
      archive.Position = localOffset;
      archive.ReadExactly(localHeader);
      if (BinaryPrimitives.ReadUInt32LittleEndian(localHeader) != 0x04034b50)
        throw new PackageException("A ZIP local entry is malformed.");
      var localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader[26..]);
      var localExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader[28..]);
      if (localExtraLength != 0 || localNameLength != nameLength ||
          (long)localOffset + localHeader.Length + localNameLength > centralOffset)
        throw new PackageException("ZIP local metadata differs from the central entry.");
      var localName = new byte[localNameLength];
      archive.ReadExactly(localName);
      if (!localName.AsSpan().SequenceEqual(centralName))
        throw new PackageException("ZIP local and central names differ.");
    }
    if (next != centralEnd) throw new PackageException("The ZIP central directory has trailing metadata.");
  }

  private static uint[] BuildCrcTable()
  {
    var table = new uint[256];
    for (uint index = 0; index < table.Length; index++)
    {
      var value = index;
      for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0u : 0xedb88320u);
      table[index] = value;
    }
    return table;
  }
}
