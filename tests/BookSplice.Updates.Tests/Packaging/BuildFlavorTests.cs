namespace BookSplice.Updates.Tests.Packaging;

public sealed class BuildFlavorTests
{
  [Fact]
  public void ReleaseAndTestMetadataAreDistinctAndStrict()
  {
    Assert.Equal(BuildFlavor.Release, UpdateBuild.Parse("Release"));
    Assert.Equal(BuildFlavor.Test, UpdateBuild.Parse("Test"));
    Assert.Throws<InvalidOperationException>(() => UpdateBuild.Parse("release"));
    Assert.Throws<InvalidOperationException>(() => UpdateBuild.Parse(""));
    Assert.Throws<InvalidOperationException>(() => UpdateBuild.Parse(null));
  }

  [Fact]
  public void ReleaseIgnoresTestVariablesButTestReadsThem()
  {
    string? Environment(string name) => "https://example.invalid/" + name;
    Assert.Null(UpdateBuild.TestVariable(BuildFlavor.Release, "BOOKSPLICE_TEST_UPDATE_ENDPOINT", Environment));
    Assert.Equal("https://example.invalid/X", UpdateBuild.TestVariable(BuildFlavor.Test, "X", Environment));
  }

  [Fact]
  public void NonReleaseBuildsTrustNoProductionKey()
  {
    // This test project references the default (Test) flavor of the updater, which embeds no production key.
    Assert.Equal(BuildFlavor.Test, UpdateBuild.Flavor);
    Assert.Empty(BookSplice.Updates.Security.EmbeddedTrust.PublicKeys);
  }
}
