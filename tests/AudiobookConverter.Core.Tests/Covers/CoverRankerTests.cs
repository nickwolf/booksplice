using AudiobookConverter.Core.Covers;

namespace AudiobookConverter.Core.Tests.Covers;

public sealed class CoverRankerTests
{
  [Fact]
  public void Rank_prefers_embedded_front_cover_over_other_external_art()
  {
    var embedded = Candidate("embedded", CoverOrigin.EmbeddedPicture, CoverSemanticType.FrontCover, 10, 10);
    var external = Candidate("external", CoverOrigin.ExternalFile, CoverSemanticType.Other, 100, 100);

    var selected = new CoverRanker().Select([external, embedded]);

    Assert.Equal("embedded", selected!.ContentHash);
  }

  [Fact]
  public void Rank_uses_ordinal_source_identity_for_equal_candidates()
  {
    var selected = new CoverRanker().Select([Candidate("b", CoverOrigin.ExternalFile, CoverSemanticType.Other, 10, 10), Candidate("a", CoverOrigin.ExternalFile, CoverSemanticType.Other, 10, 10)]);

    Assert.Equal("a", selected!.ContentHash);
  }

  private static CoverCandidate Candidate(string hash, CoverOrigin origin, CoverSemanticType semanticType, int width, int height)
    => new(hash, origin, hash, null, "image/png", width, height, semanticType, false);
}
