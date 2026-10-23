using BookSplice.Updates.Policy;

namespace BookSplice.Updates.Tests.Policy;

public sealed class ReleaseSelectionTests
{
  [Fact]
  public void HighestStableWinsRegardlessOfOrder()
  {
    var result = ReleaseSelector.Select([Candidate(1, "v1.9.0"), Candidate(2, "v1.10.0"), Candidate(3, "v1.8.0")]);
    Assert.Equal(SelectionKind.Available, result.Kind);
    Assert.Equal("v1.10.0", result.Candidate!.Tag);
    Assert.True(result.Installable);
  }

  [Fact]
  public void DraftsAndPrereleasesAreExcluded()
  {
    var result = ReleaseSelector.Select([Candidate(1, "v4.0.0", draft: true), Candidate(2, "v3.0.0-rc.1"), Candidate(3, "v2.0.0", prerelease: true), Candidate(4, "v1.0.0")]);
    Assert.Equal("v1.0.0", result.Candidate!.Tag);
  }

  [Fact]
  public void TiedHighestPrecedenceIsAmbiguous()
  {
    var result = ReleaseSelector.Select([Candidate(1, "v1.2.0+a"), Candidate(2, "v1.2.0+b")]);
    Assert.Equal(SelectionKind.Ambiguous, result.Kind);
    Assert.Null(result.Candidate);
  }

  [Fact]
  public void MissingHighestAssetsDoesNotSelectOlderRelease()
  {
    var result = ReleaseSelector.Select([Candidate(1, "v1.0.0"), Candidate(2, "v2.0.0", assets: [])]);
    Assert.Equal("v2.0.0", result.Candidate!.Tag);
    Assert.False(result.Installable);
  }

  [Fact]
  public void MissingChecksumCompanionIsUnavailable()
  {
    var assets = Candidate(1, "v2.0.0").Assets.Where(a => !a.Name.EndsWith(".sha256", StringComparison.Ordinal)).ToArray();
    var result = ReleaseSelector.Select([Candidate(1, "v2.0.0", assets: assets)]);
    Assert.Equal(SelectionKind.Available, result.Kind);
    Assert.False(result.Installable);
  }

  private static ReleaseCandidate Candidate(long id, string tag, bool draft = false, bool prerelease = false, IReadOnlyList<ReleaseAsset>? assets = null)
  {
    var version = tag.Split('+')[0].TrimStart('v');
    assets ??= [
      Asset(1, tag, $"BookSplice-{version}-win-x64.zip"),
      Asset(2, tag, $"BookSplice-{version}-win-x64.zip.sha256"),
      Asset(3, tag, $"BookSplice-{version}-win-x64.update.json"),
      Asset(4, tag, $"BookSplice-{version}-win-x64.update.sig.json")
    ];
    return new ReleaseCandidate(id, tag, SemanticVersion.ParseTag(tag), new Uri($"https://github.com/nickwolf/booksplice/releases/tag/{tag}"), "", draft, prerelease, assets);
  }

  private static ReleaseAsset Asset(long id, string tag, string name) => new(id, name, new Uri($"https://github.com/nickwolf/booksplice/releases/download/{tag}/{name}"), 100);
}
