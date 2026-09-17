using System.Security.Cryptography;
using BookSplice.Core.Discovery;

namespace BookSplice.Core.Covers;

public sealed class CoverDiscoverer : ICoverDiscoverer
{
  /// <summary>Maximum accepted width or height in pixels.</summary>
  public const int MaximumPixelsPerAxis = 16_384;
  /// <summary>Maximum estimated RGBA decoded memory for one cover.</summary>
  public const long MaximumDecodedMemoryBytes = 256L * 1024 * 1024;
  /// <summary>Maximum accepted compressed cover payload size.</summary>
  public const long MaximumEncodedPayloadBytes = 64L * 1024 * 1024;
  /// <summary>Maximum JPEG prefix examined while locating a supported SOF marker.</summary>
  public const int MaximumJpegHeaderBytes = 64 * 1024;
  /// <summary>Maximum entries retained while sorting one directory.</summary>
  public const int MaximumDirectoryEntries = 100_000;

  private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };
  private static readonly HashSet<string> CanonicalNames = new(StringComparer.OrdinalIgnoreCase) { "cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.png", "front.jpg", "front.png" };
  private readonly ICoverPayloadOpener _payloadOpener;
  private readonly ICoverDirectoryEnumerator _directoryEnumerator;
  private readonly CoverRanker _ranker;

  public CoverDiscoverer(ICoverPayloadOpener? payloadOpener = null, CoverRanker? ranker = null, ICoverDirectoryEnumerator? directoryEnumerator = null)
  {
    _payloadOpener = payloadOpener ?? new FileSystemCoverPayloadOpener();
    _ranker = ranker ?? new CoverRanker();
    _directoryEnumerator = directoryEnumerator ?? new FileSystemCoverDirectoryEnumerator();
  }

  public Task<CoverDiscoveryResult> DiscoverAsync(string sourceRoot, IReadOnlyList<SourceFile> files, CancellationToken cancellationToken)
    => DiscoverAsync(sourceRoot, files, new CoverDiscoveryOptions(), cancellationToken);

  public async Task<CoverDiscoveryResult> DiscoverAsync(string sourceRoot, IReadOnlyList<SourceFile> files, CoverDiscoveryOptions options, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
    ArgumentNullException.ThrowIfNull(files);
    ArgumentNullException.ThrowIfNull(options);
    cancellationToken.ThrowIfCancellationRequested();
    var rejections = new List<CoverRejection>();
    var root = ResolveRoot(sourceRoot, rejections);
    if (root is null) return new CoverDiscoveryResult([], rejections, null);
    var references = ExternalPayloads(root, rejections, cancellationToken).Concat(EmbeddedPayloads(files));
    var candidates = new List<CoverCandidate>();
    foreach (var payload in references.OrderBy(reference => reference.StableIdentity, StringComparer.Ordinal))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var inspected = await InspectAsync(payload, cancellationToken).ConfigureAwait(false);
      if (inspected.Rejection is { } rejection) { rejections.Add(rejection); continue; }
      candidates.Add(new CoverCandidate(inspected.Hash!, payload.Origin, payload.SourcePath, payload.EmbeddedPictureIndex, inspected.ContentType!, inspected.Width, inspected.Height, SemanticType(payload), payload.Origin == CoverOrigin.ExternalFile && string.Equals(Path.GetDirectoryName(payload.SourcePath), root, StringComparison.OrdinalIgnoreCase) && CanonicalNames.Contains(Path.GetFileName(payload.SourcePath))));
    }
    var representatives = candidates.GroupBy(candidate => candidate.ContentHash, StringComparer.OrdinalIgnoreCase).Select(group => _ranker.Select(group)!).ToArray();
    var ranked = _ranker.Rank(representatives, options.PriorSelectedContentHash);
    return new CoverDiscoveryResult(ranked, rejections.OrderBy(rejection => rejection.SourceIdentity, StringComparer.Ordinal).ToArray(), ranked.Count == 0 ? null : ranked[0]);
  }

  private static string? ResolveRoot(string sourceRoot, List<CoverRejection> rejections)
  {
    var full = Path.GetFullPath(sourceRoot);
    try
    {
      if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
      {
        rejections.Add(new("cover.reparse-point-skipped", "A reparse point was skipped during cover discovery.", full));
        return null;
      }
    }
    catch (IOException) { return full; }
    catch (UnauthorizedAccessException) { return full; }
    return File.Exists(full) ? Path.GetDirectoryName(full)! : full;
  }

  private IEnumerable<CoverPayloadReference> ExternalPayloads(string root, List<CoverRejection> rejections, CancellationToken cancellationToken)
  {
    if (!Directory.Exists(root)) yield break;
    foreach (var path in Enumerate(root, root, rejections, _directoryEnumerator, cancellationToken))
    {
      if (ImageExtensions.Contains(Path.GetExtension(path))) yield return new(CoverOrigin.ExternalFile, path, null, path, SourceRoot: root);
    }
  }

  private static IEnumerable<string> Enumerate(string root, string directory, List<CoverRejection> rejections, ICoverDirectoryEnumerator directoryEnumerator, CancellationToken cancellationToken)
  {
    List<string> entries = [];
    try
    {
      foreach (var entry in directoryEnumerator.EnumerateFileSystemEntries(directory))
      {
        cancellationToken.ThrowIfCancellationRequested();
        if (entries.Count == MaximumDirectoryEntries) { rejections.Add(new("cover.directory-too-large", "A directory exceeded the safe cover path limit.", directory)); yield break; }
        entries.Add(entry);
      }
      entries.Sort(StringComparer.Ordinal);
    }
    catch (IOException) { rejections.Add(new("cover.path-unreadable", "A cover path could not be read.", directory)); yield break; }
    catch (UnauthorizedAccessException) { rejections.Add(new("cover.path-unreadable", "A cover path could not be read.", directory)); yield break; }
    foreach (var entry in entries)
    {
      cancellationToken.ThrowIfCancellationRequested();
      FileAttributes attributes;
      try { attributes = File.GetAttributes(entry); }
      catch (IOException) { rejections.Add(new("cover.path-unreadable", "A cover path could not be read.", entry)); continue; }
      catch (UnauthorizedAccessException) { rejections.Add(new("cover.path-unreadable", "A cover path could not be read.", entry)); continue; }
      if ((attributes & FileAttributes.ReparsePoint) != 0) { rejections.Add(new("cover.reparse-point-skipped", "A reparse point was skipped during cover discovery.", entry)); continue; }
      if ((attributes & FileAttributes.Directory) != 0)
      {
        if (!IsUnderRoot(root, entry)) { rejections.Add(new("cover.path-outside-root", "A cover path outside the source root was skipped.", entry)); continue; }
        foreach (var path in Enumerate(root, entry, rejections, directoryEnumerator, cancellationToken)) yield return path;
      }
      else if (IsUnderRoot(root, entry)) yield return Path.GetFullPath(entry);
    }
  }

  private static bool IsUnderRoot(string root, string path)
  {
    var relative = Path.GetRelativePath(root, path);
    return !Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal);
  }

  private static IEnumerable<CoverPayloadReference> EmbeddedPayloads(IEnumerable<SourceFile> files)
    => files.SelectMany(file => file.ProbeResult.AttachedPictures.Select(picture => new CoverPayloadReference(CoverOrigin.EmbeddedPicture, file.FullPath, picture.Index, $"{file.FullPath}#{picture.Index}", IsFrontCover(picture) ? CoverSemanticType.FrontCover : CoverSemanticType.Other)));

  private static CoverSemanticType SemanticType(CoverPayloadReference payload)
    => payload.Origin == CoverOrigin.ExternalFile
      ? CanonicalNames.Contains(Path.GetFileName(payload.SourcePath)) ? CoverSemanticType.FrontCover : CoverSemanticType.Other
      : payload.SemanticType;

  private static bool IsFrontCover(Analysis.MediaAttachedPicture picture)
    => picture.RawTags.TryGetValue("title", out var title) && title.Split([' ', '-', '_', '.', '/'], StringSplitOptions.RemoveEmptyEntries).Any(part => string.Equals(part, "cover", StringComparison.OrdinalIgnoreCase) || string.Equals(part, "front", StringComparison.OrdinalIgnoreCase));

  private async Task<Inspection> InspectAsync(CoverPayloadReference payload, CancellationToken cancellationToken)
  {
    try
    {
      await using var stream = await _payloadOpener.OpenReadAsync(payload, cancellationToken).ConfigureAwait(false);
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      var buffer = new byte[64 * 1024];
      long length = 0;
      async ValueTask<bool> ReadExactAsync(Memory<byte> destination)
      {
        var offset = 0;
        while (offset < destination.Length)
        {
          var read = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
          if (read == 0) return false;
          checked { length += read; }
          if (length > MaximumEncodedPayloadBytes) throw new PayloadTooLargeException();
          hash.AppendData(destination.Span.Slice(offset, read));
          offset += read;
        }
        return true;
      }

      var prefix = new byte[8];
      if (!await ReadExactAsync(prefix).ConfigureAwait(false)) return Inspection.Reject("cover.invalid-header", payload.StableIdentity);
      if (prefix.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
      {
        var png = await TryReadPngAsync(ReadExactAsync, buffer).ConfigureAwait(false);
        if (!png.Valid) return Inspection.Reject("cover.invalid-header", payload.StableIdentity);
        return ValidateDimensions(hash, png.ContentType, png.Width, png.Height, payload.StableIdentity);
      }

      using var header = new MemoryStream(MaximumJpegHeaderBytes);
      header.Write(prefix);
      var previous = prefix[^1];
      var sawJpegEoi = false;
      while (true)
      {
        var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (read == 0) break;
        checked { length += read; }
        if (length > MaximumEncodedPayloadBytes) return Inspection.Reject("cover.payload-too-large", payload.StableIdentity);
        hash.AppendData(buffer, 0, read);
        for (var index = 0; index < read; index++) { if (previous == 255 && buffer[index] == 217) sawJpegEoi = true; previous = buffer[index]; }
        var remaining = MaximumJpegHeaderBytes - (int)header.Length;
        if (remaining > 0) header.Write(buffer, 0, Math.Min(remaining, read));
      }
      if (!TryReadDimensions(header.ToArray(), sawJpegEoi, out var contentType, out var width, out var height)) return Inspection.Reject("cover.invalid-header", payload.StableIdentity);
      return ValidateDimensions(hash, contentType, width, height, payload.StableIdentity);
    }
    catch (PayloadTooLargeException) { return Inspection.Reject("cover.payload-too-large", payload.StableIdentity); }
    catch (OperationCanceledException) { throw; }
    catch (Exception) { return Inspection.Reject("cover.payload-unreadable", payload.StableIdentity); }
  }

  private static Inspection ValidateDimensions(IncrementalHash hash, string? contentType, int width, int height, string identity)
  {
    if (width > MaximumPixelsPerAxis || height > MaximumPixelsPerAxis) return Inspection.Reject("cover.dimension-too-large", identity);
    long decodedBytes;
    try { decodedBytes = checked((long)width * height * 4); }
    catch (OverflowException) { return Inspection.Reject("cover.decoded-memory-too-large", identity); }
    if (decodedBytes > MaximumDecodedMemoryBytes) return Inspection.Reject("cover.decoded-memory-too-large", identity);
    return new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), contentType, width, height, null);
  }

  private static bool TryReadDimensions(byte[] bytes, bool sawJpegEoi, out string? contentType, out int width, out int height)
  {
    contentType = null; width = height = 0;
    if (bytes.Length < 4 || bytes[0] != 255 || bytes[1] != 216) return false;
    var offset = 2;
    while (offset + 4 <= bytes.Length)
    {
      if (bytes[offset++] != 255) return false;
      while (offset < bytes.Length && bytes[offset] == 255) offset++;
      if (offset >= bytes.Length) return false;
      var marker = bytes[offset++];
      if (marker is 216 or 217) continue;
      if (offset + 2 > bytes.Length) return false;
      var length = (bytes[offset] << 8) | bytes[offset + 1];
      if (length < 2 || offset + length > bytes.Length) return false;
      if (marker is 192 or 193 or 194 or 195 or 197 or 198 or 199 or 201 or 202 or 203 or 205 or 206 or 207)
      {
        if (length < 8) return false;
        height = (bytes[offset + 3] << 8) | bytes[offset + 4]; width = (bytes[offset + 5] << 8) | bytes[offset + 6]; contentType = "image/jpeg"; return width > 0 && height > 0 && sawJpegEoi;
      }
      offset += length;
    }
    return false;
  }

  private static int ReadInt32BigEndian(ReadOnlySpan<byte> value) => (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];
  private static async ValueTask<PngInspection> TryReadPngAsync(Func<Memory<byte>, ValueTask<bool>> readExactAsync, byte[] buffer)
  {
    var seenIhdr = false;
    var seenIdat = false;
    var width = 0;
    var height = 0;
    while (true)
    {
      var chunkHeader = new byte[8];
      if (!await readExactAsync(chunkHeader).ConfigureAwait(false)) return new(false, null, 0, 0);
      var length = ReadUInt32BigEndian(chunkHeader);
      if (length > MaximumEncodedPayloadBytes || length > int.MaxValue) return new(false, null, 0, 0);
      var type = chunkHeader[4..8];
      var crc = UpdateCrc(0xffffffffU, type);
      var ihdr = length == 13 && type.AsSpan().SequenceEqual("IHDR"u8) ? new byte[13] : null;
      var copied = 0;
      var remaining = (int)length;
      while (remaining > 0)
      {
        var count = Math.Min(remaining, buffer.Length);
        if (!await readExactAsync(buffer.AsMemory(0, count)).ConfigureAwait(false)) return new(false, null, 0, 0);
        crc = UpdateCrc(crc, buffer.AsSpan(0, count));
        if (ihdr is not null) { buffer.AsSpan(0, count).CopyTo(ihdr.AsSpan(copied)); copied += count; }
        remaining -= count;
      }
      var crcBytes = new byte[4];
      if (!await readExactAsync(crcBytes).ConfigureAwait(false)) return new(false, null, 0, 0);
      if (~crc != ReadUInt32BigEndian(crcBytes)) return new(false, null, 0, 0);
      if (!seenIhdr)
      {
        if (ihdr is null) return new(false, null, 0, 0);
        width = ReadInt32BigEndian(ihdr.AsSpan(0, 4));
        height = ReadInt32BigEndian(ihdr.AsSpan(4, 4));
        if (width <= 0 || height <= 0) return new(false, null, 0, 0);
        seenIhdr = true;
      }
      if (type.AsSpan().SequenceEqual("IDAT"u8)) seenIdat = true;
      if (type.AsSpan().SequenceEqual("IEND"u8))
      {
        if (!seenIdat || length != 0) return new(false, null, 0, 0);
        var trailing = new byte[1];
        return await readExactAsync(trailing).ConfigureAwait(false) ? new(false, null, 0, 0) : new(true, "image/png", width, height);
      }
    }
  }

  private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> value) => ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];
  private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
  {
    var crc = 0xffffffffU;
    crc = UpdateCrc(crc, type);
    crc = UpdateCrc(crc, data);
    return ~crc;
  }
  private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
  {
    foreach (var value in bytes) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0xedb88320U); }
    return crc;
  }
  private sealed record PngInspection(bool Valid, string? ContentType, int Width, int Height);
  private sealed class PayloadTooLargeException : Exception;
  private sealed record Inspection(string? Hash, string? ContentType, int Width, int Height, CoverRejection? Rejection)
  {
    public static Inspection Reject(string code, string identity) => new(null, null, 0, 0, new CoverRejection(code, "The cover image was rejected during inspection.", identity));
  }
}
