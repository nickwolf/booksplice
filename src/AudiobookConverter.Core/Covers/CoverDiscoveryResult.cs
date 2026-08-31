namespace AudiobookConverter.Core.Covers;

public sealed record CoverRejection(string Code, string Message, string SourceIdentity);

public sealed record CoverDiscoveryOptions(string? PriorSelectedContentHash = null);

public sealed record CoverDiscoveryResult(
  IReadOnlyList<CoverCandidate> Candidates,
  IReadOnlyList<CoverRejection> Rejections,
  CoverCandidate? Selected)
{
  public string? SelectedContentHash => Selected?.ContentHash;
}

public interface ICoverDiscoverer
{
  Task<CoverDiscoveryResult> DiscoverAsync(string sourceRoot, IReadOnlyList<Discovery.SourceFile> files, CancellationToken cancellationToken);
}
