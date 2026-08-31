namespace AudiobookConverter.Core.Covers;

public enum CoverOrigin
{
  ExternalFile,
  EmbeddedPicture,
}

public enum CoverSemanticType
{
  Other,
  FrontCover,
}

public sealed record CoverCandidate(
  string ContentHash,
  CoverOrigin Origin,
  string SourcePath,
  int? EmbeddedPictureIndex,
  string ContentType,
  int Width,
  int Height,
  CoverSemanticType SemanticType,
  bool IsCanonicalRootFile)
{
  public string SourceIdentity => EmbeddedPictureIndex is { } index ? $"{SourcePath}#{index}" : SourcePath;
}

public sealed record CoverPayloadReference(CoverOrigin Origin, string SourcePath, int? EmbeddedPictureIndex, string StableIdentity, CoverSemanticType SemanticType = CoverSemanticType.Other, string? SourceRoot = null);

public interface ICoverPayloadOpener
{
  ValueTask<Stream> OpenReadAsync(CoverPayloadReference payload, CancellationToken cancellationToken);
}

public sealed class FileSystemCoverPayloadOpener : ICoverPayloadOpener
{
  public ValueTask<Stream> OpenReadAsync(CoverPayloadReference payload, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (payload.Origin != CoverOrigin.ExternalFile) throw new NotSupportedException("Embedded cover payloads require an adapter.");
    var fullPath = Path.GetFullPath(payload.SourcePath);
    if (payload.SourceRoot is { } root && !IsUnderRoot(Path.GetFullPath(root), fullPath)) throw new IOException("The cover path is outside the source root.");
    if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("A reparse-point cover payload is not allowed.");
    var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
    try
    {
      if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("A reparse-point cover payload is not allowed.");
      return ValueTask.FromResult<Stream>(stream);
    }
    catch { stream.Dispose(); throw; }
  }

  private static bool IsUnderRoot(string root, string path)
  {
    var relative = Path.GetRelativePath(root, path);
    return !Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal);
  }
}
