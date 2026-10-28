using System.Diagnostics;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Tests.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Windows;

[Collection("WindowsOwnedStorage")]
public sealed class FinalPathTests : IDisposable
{
  private readonly string _root = Path.Combine(FinalPath.ResolveDirectory(Path.GetTempPath()),
    "booksplice-final-" + Guid.NewGuid().ToString("N"));
  private string Real => Path.Combine(_root, "releases", "app");
  private string Current => Path.Combine(_root, "current");
  private string Data => Path.Combine(_root, "data");

  public FinalPathTests()
  {
    Directory.CreateDirectory(Data);
    OriginalPackageFixture.Write(Real, "0.1.0");
    Junction(Current, Real);
  }

  public void Dispose()
  {
    Directory.Delete(Current);
    Directory.Delete(_root, true);
  }

  [Fact]
  public void JunctionResolvesToTheRealFolder()
  {
    Assert.Equal(Real, FinalPath.ResolveDirectory(Current), ignoreCase: true);
    Assert.Equal(Real, FinalPath.ResolveDirectory(Real), ignoreCase: true);
  }

  [Fact]
  public void MissingFolderFallsBackToTheGivenPath()
  {
    var missing = Path.Combine(_root, "missing");
    Assert.Equal(missing, FinalPath.ResolveDirectory(missing), ignoreCase: true);
  }

  [Theory]
  [InlineData(@"\\?\C:\a\b", @"C:\a\b")]
  [InlineData(@"\\?\UNC\server\share\a", @"\\server\share\a")]
  [InlineData(@"C:\a", @"C:\a")]
  public void PrefixIsStripped(string input, string expected) =>
    Assert.Equal(expected, FinalPath.StripPrefix(input));

  [Fact]
  public void LinkedLaunchPathIsRejectedButTheResolvedPathCreatesTheInstallation()
  {
    Assert.ThrowsAny<Exception>(() => InstallationOpener.Create(Current, Data, "0.1.0", null));
    var created = InstallationOpener.Create(FinalPath.ResolveDirectory(Current), Data, "0.1.0", null);
    var binding = InstallationRegistry.Open(Data, create: false)!.Resolve(
      created.Record.OriginalExecutablePath, created.Record.OriginalIdentity,
      created.Record.OriginalExecutableIdentity);
    Assert.Equal(created.Layout.ManagedRoot.Path, binding!.ManagedRootPath);
    Assert.Equal(Real, Path.GetDirectoryName(created.Record.OriginalExecutablePath), ignoreCase: true);
  }

  [Fact]
  public void ReparsePointInsideManagedFolderIsStillRejected()
  {
    var created = InstallationOpener.Create(Real, Data, "0.1.0", null);
    var link = Path.Combine(created.Layout.Versions.Path, "linked");
    Junction(link, Data);
    try
    {
      Assert.Throws<NotSupportedException>(() => SafeDirectoryHandle.OpenCanonical(link));
    }
    finally { Directory.Delete(link); }
  }

  private static void Junction(string link, string target)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(link)!);
    using var process = Process.Start(new ProcessStartInfo("cmd.exe")
    {
      ArgumentList = { "/d", "/c", "mklink", "/J", link, target },
      UseShellExecute = false,
      CreateNoWindow = true
    })!;
    process.WaitForExit();
    Assert.Equal(0, process.ExitCode);
  }
}
