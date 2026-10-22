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
  public void ReleaseIgnoresRehearsalHooksButTestHonorsThem()
  {
    var directory = Path.GetTempPath();
    string? Environment(string name) => name is "BOOKSPLICE_TEST_RELEASE_DIR" or "BOOKSPLICE_TEST_PROGRAMS_DIR" ? directory : null;
    Assert.Null(global::BookSplice.Updates.Transport.TestReleaseSource.Create(BuildFlavor.Release, Environment));
    Assert.NotNull(global::BookSplice.Updates.Transport.TestReleaseSource.Create(BuildFlavor.Test, Environment));
    Assert.Null(global::BookSplice.Updates.Transport.TestReleaseSource.Create(BuildFlavor.Test, _ => null));
    Assert.NotEqual(directory, UpdateBuild.ProgramsDirectory(BuildFlavor.Release, Environment));
    Assert.Equal(directory, UpdateBuild.ProgramsDirectory(BuildFlavor.Test, Environment));
    Assert.False(UpdateBuild.PassesToChildren(BuildFlavor.Release, "BOOKSPLICE_TEST_NO_READY"));
    Assert.True(UpdateBuild.PassesToChildren(BuildFlavor.Test, "BOOKSPLICE_TEST_NO_READY"));
    Assert.False(UpdateBuild.PassesToChildren(BuildFlavor.Test, "BOOKSPLICE_LOCAL_APP_DATA"));
  }

  [Fact]
  public void NonReleaseBuildsTrustNoProductionKey()
  {
    // This test project references the default (Test) flavor of the updater, which embeds no production key.
    Assert.Equal(BuildFlavor.Test, UpdateBuild.Flavor);
    Assert.Empty(BookSplice.Updates.Security.EmbeddedTrust.PublicKeys);
  }
}
