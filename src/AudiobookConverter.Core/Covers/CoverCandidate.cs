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

public sealed record CoverPayloadReference(CoverOrigin Origin, string SourcePath, int? EmbeddedPictureIndex, string StableIdentity, CoverSemanticType SemanticType = CoverSemanticType.Other);

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
    return ValueTask.FromResult<Stream>(new FileStream(payload.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan));
  }
}
