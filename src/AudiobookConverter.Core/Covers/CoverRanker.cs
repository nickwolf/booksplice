namespace AudiobookConverter.Core.Covers;

public sealed class CoverRanker
{
  private readonly StringComparer _identityComparer = StringComparer.Ordinal;

  public IReadOnlyList<CoverCandidate> Rank(IEnumerable<CoverCandidate> candidates, string? priorSelectedContentHash = null)
    => candidates
      .OrderBy(candidate => HasPriorSelection(candidate, priorSelectedContentHash) ? 0 : 1)
      .ThenBy(candidate => candidate.IsCanonicalRootFile && candidate.Origin == CoverOrigin.ExternalFile ? 0 : 1)
      .ThenBy(candidate => candidate.Origin == CoverOrigin.EmbeddedPicture && candidate.SemanticType == CoverSemanticType.FrontCover ? 0 : 1)
      .ThenBy(candidate => candidate.SemanticType == CoverSemanticType.FrontCover ? 0 : 1)
      .ThenByDescending(candidate => (long)candidate.Width * candidate.Height)
      .ThenBy(candidate => PathDepth(candidate.SourcePath))
      .ThenBy(candidate => candidate.SourceIdentity, _identityComparer)
      .ToArray();

  public CoverCandidate? Select(IEnumerable<CoverCandidate> candidates, string? priorSelectedContentHash = null)
  {
    var ranked = Rank(candidates, priorSelectedContentHash);
    return ranked.Count == 0 ? null : ranked[0];
  }

  private static bool HasPriorSelection(CoverCandidate candidate, string? priorSelectedContentHash)
    => !string.IsNullOrWhiteSpace(priorSelectedContentHash) && string.Equals(candidate.ContentHash, priorSelectedContentHash, StringComparison.OrdinalIgnoreCase);

  private static int PathDepth(string path) => path.Count(character => character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar);
}
