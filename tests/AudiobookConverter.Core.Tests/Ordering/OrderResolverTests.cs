using AudiobookConverter.Core.Analysis;
using AudiobookConverter.Core.Discovery;
using AudiobookConverter.Core.Ordering;

namespace AudiobookConverter.Core.Tests.Ordering;

public sealed class OrderResolverTests
{
  private readonly OrderResolver _resolver = new();

  [Fact]
  public void Resolve_orders_complete_disc_and_track_metadata()
  {
    var resolution = _resolver.Resolve([
      File("03.m4a", ("DISCNUMBER", "2"), ("TRACKNUMBER", "1")),
      File("02.m4a", ("DISCNUMBER", "1"), ("TRACKNUMBER", "2")),
      File("01.m4a", ("DISCNUMBER", "1"), ("TRACKNUMBER", "1"))
    ]);

    Assert.Equal(OrderStatus.Resolved, resolution.Status);
    Assert.Equal(OrderCandidateId.Metadata, resolution.SelectedCandidate!.Id);
    Assert.Equal((IEnumerable<string>)["01.m4a", "02.m4a", "03.m4a"], resolution.SelectedCandidate.FileIds);
    Assert.True(resolution.SelectedCandidate.IsCredible);
  }

  [Fact]
  public void Resolve_accepts_complete_unique_track_numbers_as_single_disc_metadata()
  {
    var resolution = _resolver.Resolve([
      File("03.m4a", ("TRACK", "3/3")),
      File("01.m4a", ("TRACK", "1/3")),
      File("02.m4a", ("TRACK", "2/3"))
    ]);

    Assert.Equal(OrderStatus.Resolved, resolution.Status);
    Assert.Equal(OrderCandidateId.Metadata, resolution.SelectedCandidate!.Id);
    Assert.Equal((IEnumerable<string>)["01.m4a", "02.m4a", "03.m4a"], resolution.SelectedCandidate.FileIds);
  }

  [Fact]
  public void Resolve_keeps_track_resets_without_disc_data_as_not_credible_evidence()
  {
    var resolution = _resolver.Resolve([
      File("CD1/01.m4a", ("TRACKNUMBER", "1")),
      File("CD2/01.m4a", ("TRACKNUMBER", "1"))
    ]);

    var metadata = Assert.Single(resolution.Candidates, candidate => candidate.Id == OrderCandidateId.Metadata);
    Assert.False(metadata.IsCredible);
    Assert.Contains(metadata.Evidence, evidence => evidence.Code == "ordering.metadata.duplicate-track");
    Assert.Equal(OrderCandidateId.NaturalPath, resolution.SelectedCandidate!.Id);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("0")]
  [InlineData("unknown")]
  public void Resolve_keeps_missing_or_malformed_track_data_as_not_credible_evidence(string? track)
  {
    var tags = track is null ? Array.Empty<(string Key, string Value)>() : [("TRACKNUMBER", track)];
    var resolution = _resolver.Resolve([File("a.m4a", tags), File("b.m4a", ("TRACKNUMBER", "2"))]);

    var metadata = Assert.Single(resolution.Candidates, candidate => candidate.Id == OrderCandidateId.Metadata);
    Assert.False(metadata.IsCredible);
    Assert.NotEmpty(metadata.Evidence);
    Assert.Equal(OrderCandidateId.NaturalPath, resolution.SelectedCandidate!.Id);
  }

  [Fact]
  public void Resolve_requires_a_decision_when_complete_tags_disagree_with_natural_paths()
  {
    var resolution = _resolver.Resolve([
      File("02.m4a", ("TRACKNUMBER", "1")),
      File("01.m4a", ("TRACKNUMBER", "2"))
    ]);

    Assert.Equal(OrderStatus.NeedsDecision, resolution.Status);
    Assert.Null(resolution.SelectedCandidate);
    Assert.Equal(2, resolution.Candidates.Count(candidate => candidate.IsCredible));
  }

  [Fact]
  public void Resolve_keeps_gapped_disc_numbers_as_not_credible_evidence()
  {
    var resolution = _resolver.Resolve([
      File("01.m4a", ("DISC", "1"), ("TRACK", "1")),
      File("03.m4a", ("DISC", "3"), ("TRACK", "1"))
    ]);

    var metadata = Assert.Single(resolution.Candidates, candidate => candidate.Id == OrderCandidateId.Metadata);
    Assert.False(metadata.IsCredible);
    Assert.Contains(metadata.Evidence, evidence => evidence.Code == "ordering.metadata.incoherent-disc-sequence");
  }

  [Fact]
  public void Resolve_keeps_gapped_track_numbers_within_a_disc_as_not_credible_evidence()
  {
    var resolution = _resolver.Resolve([
      File("01.m4a", ("DISC", "1"), ("TRACK", "1")),
      File("03.m4a", ("DISC", "1"), ("TRACK", "3"))
    ]);

    var metadata = Assert.Single(resolution.Candidates, candidate => candidate.Id == OrderCandidateId.Metadata);
    Assert.False(metadata.IsCredible);
    Assert.Contains(metadata.Evidence, evidence => evidence.Code == "ordering.metadata.incoherent-track-sequence");
  }


  [Fact]
  public void Resolve_raises_confidence_when_complete_tags_agree_with_natural_paths()
  {
    var resolution = _resolver.Resolve([
      File("CD3/01.m4a", ("DISC", "3"), ("TRACK", "1")),
      File("CD1/01.m4a", ("DISC", "1"), ("TRACK", "1")),
      File("CD2/01.m4a", ("DISC", "2"), ("TRACK", "1"))
    ]);

    Assert.Equal(OrderStatus.Resolved, resolution.Status);
    Assert.Equal(OrderConfidence.High, resolution.SelectedCandidate!.Confidence);
    Assert.Single(resolution.AlternateCandidates);
  }

  [Fact]
  public void Resolve_uses_natural_paths_when_disc_specific_album_tags_are_not_order_metadata()
  {
    var resolution = _resolver.Resolve([
      File("CD2/01.m4a", ("ALBUM", "Collection 2")),
      File("CD1/01.m4a", ("ALBUM", "Collection 1")),
      File("CD3/01.m4a", ("ALBUM", "Collection 3"))
    ]);

    Assert.Equal(OrderStatus.Resolved, resolution.Status);
    Assert.Equal(OrderCandidateId.NaturalPath, resolution.SelectedCandidate!.Id);
    Assert.Equal((IEnumerable<string>)["CD1/01.m4a", "CD2/01.m4a", "CD3/01.m4a"], resolution.SelectedCandidate.FileIds);
  }

  private static SourceFile File(string relativePath, params (string Key, string Value)[] tags)
    => new(relativePath, relativePath, new MediaProbeResult([], [], [], new TagCollection(tags.Select(tag => new KeyValuePair<string, string>(tag.Key, tag.Value))), new Dictionary<string, string>(), [], 1));
}
