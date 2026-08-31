using System.Security.Cryptography;
using AudiobookConverter.Core.Discovery;

namespace AudiobookConverter.Core.Covers;

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

  private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };
  private static readonly HashSet<string> CanonicalNames = new(StringComparer.OrdinalIgnoreCase) { "cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.png", "front.jpg", "front.png" };
  private readonly ICoverPayloadOpener _payloadOpener;
  private readonly CoverRanker _ranker;

  public CoverDiscoverer(ICoverPayloadOpener? payloadOpener = null, CoverRanker? ranker = null)
  {
    _payloadOpener = payloadOpener ?? new FileSystemCoverPayloadOpener();
    _ranker = ranker ?? new CoverRanker();
  }

  public Task<CoverDiscoveryResult> DiscoverAsync(string sourceRoot, IReadOnlyList<SourceFile> files, CancellationToken cancellationToken)
    => DiscoverAsync(sourceRoot, files, new CoverDiscoveryOptions(), cancellationToken);

  public async Task<CoverDiscoveryResult> DiscoverAsync(string sourceRoot, IReadOnlyList<SourceFile> files, CoverDiscoveryOptions options, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
    ArgumentNullException.ThrowIfNull(files);
    ArgumentNullException.ThrowIfNull(options);
    cancellationToken.ThrowIfCancellationRequested();
    var root = ResolveRoot(sourceRoot);
    var rejections = new List<CoverRejection>();
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

  private static string ResolveRoot(string sourceRoot)
  {
    var full = Path.GetFullPath(sourceRoot);
    return File.Exists(full) ? Path.GetDirectoryName(full)! : full;
  }

  private static IEnumerable<CoverPayloadReference> ExternalPayloads(string root, List<CoverRejection> rejections, CancellationToken cancellationToken)
  {
    if (!Directory.Exists(root)) yield break;
    foreach (var path in Enumerate(root, root, rejections, cancellationToken))
    {
      if (ImageExtensions.Contains(Path.GetExtension(path))) yield return new(CoverOrigin.ExternalFile, path, null, path);
    }
  }

  private static IEnumerable<string> Enumerate(string root, string directory, List<CoverRejection> rejections, CancellationToken cancellationToken)
  {
    IEnumerable<string> entries;
    try { entries = Directory.EnumerateFileSystemEntries(directory).OrderBy(path => path, StringComparer.Ordinal).ToArray(); }
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
        foreach (var path in Enumerate(root, entry, rejections, cancellationToken)) yield return path;
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
    => picture.RawTags.TryGetValue("title", out var title) && (title.Contains("cover", StringComparison.OrdinalIgnoreCase) || title.Contains("front", StringComparison.OrdinalIgnoreCase));

  private async Task<Inspection> InspectAsync(CoverPayloadReference payload, CancellationToken cancellationToken)
  {
    try
    {
      await using var stream = await _payloadOpener.OpenReadAsync(payload, cancellationToken).ConfigureAwait(false);
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      var buffer = new byte[64 * 1024];
      using var header = new MemoryStream(MaximumJpegHeaderBytes);
      long length = 0;
      int read;
      while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
      {
        checked { length += read; }
        if (length > MaximumEncodedPayloadBytes) return Inspection.Reject("cover.payload-too-large", payload.StableIdentity);
        hash.AppendData(buffer, 0, read);
        var remaining = MaximumJpegHeaderBytes - (int)header.Length;
        if (remaining > 0) header.Write(buffer, 0, Math.Min(remaining, read));
      }
      var bytes = header.ToArray();
      if (!TryReadDimensions(bytes, out var contentType, out var width, out var height)) return Inspection.Reject("cover.invalid-header", payload.StableIdentity);
      if (width > MaximumPixelsPerAxis || height > MaximumPixelsPerAxis) return Inspection.Reject("cover.dimension-too-large", payload.StableIdentity);
      long decodedBytes;
      try { decodedBytes = checked((long)width * height * 4); }
      catch (OverflowException) { return Inspection.Reject("cover.decoded-memory-too-large", payload.StableIdentity); }
      if (decodedBytes > MaximumDecodedMemoryBytes) return Inspection.Reject("cover.decoded-memory-too-large", payload.StableIdentity);
      return new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), contentType, width, height, null);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception) { return Inspection.Reject("cover.payload-unreadable", payload.StableIdentity); }
  }

  private static bool TryReadDimensions(byte[] bytes, out string? contentType, out int width, out int height)
  {
    contentType = null; width = height = 0;
    if (bytes.Length >= 24 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71 && bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10 && bytes[12] == (byte)'I' && bytes[13] == (byte)'H' && bytes[14] == (byte)'D' && bytes[15] == (byte)'R')
    {
      width = ReadInt32BigEndian(bytes.AsSpan(16, 4)); height = ReadInt32BigEndian(bytes.AsSpan(20, 4)); contentType = "image/png"; return width > 0 && height > 0;
    }
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
        height = (bytes[offset + 3] << 8) | bytes[offset + 4]; width = (bytes[offset + 5] << 8) | bytes[offset + 6]; contentType = "image/jpeg"; return width > 0 && height > 0;
      }
      offset += length;
    }
    return false;
  }

  private static int ReadInt32BigEndian(ReadOnlySpan<byte> value) => (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];
  private sealed record Inspection(string? Hash, string? ContentType, int Width, int Height, CoverRejection? Rejection)
  {
    public static Inspection Reject(string code, string identity) => new(null, null, 0, 0, new CoverRejection(code, "The cover image was rejected during inspection.", identity));
  }
}
