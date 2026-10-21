using BookSplice.Updates.Handover;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Storage;

[Collection("WindowsOwnedStorage")]
public sealed class InstallationOpenerTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(),
    "booksplice-opener-" + Guid.NewGuid().ToString("N"));
  private string Original => Path.Combine(_root, "BookSplice");
  private string Data => Path.Combine(_root, "data");
  private string Programs => Path.Combine(_root, "Programs");

  public InstallationOpenerTests()
  {
    Directory.CreateDirectory(Data);
    Directory.CreateDirectory(Programs);
    OriginalPackageFixture.Write(Original, "0.1.0");
  }

  public void Dispose() => Directory.Delete(_root, true);

  [Fact]
  public void CreateRecordsTheOriginalAndOpenRestoresIt()
  {
    var created = InstallationOpener.Create(Original, Data, "0.1.0", Programs);
    var opened = InstallationOpener.Open(created.Layout.ManagedRoot.Path, programsDirectory: Programs);
    Assert.Equal(created.Layout.InstallationId, opened.Layout.InstallationId);
    var current = opened.State.Read<CurrentTarget>(Path.Combine(opened.RecordsPath, "current.json"));
    Assert.Equal(TargetKind.Original, current.Value!.Current.Kind);
    Assert.Null(current.Value.Previous);
    var binding = InstallationRegistry.Open(Data, create: false)!.Resolve(
      opened.Record.OriginalExecutablePath, opened.Record.OriginalIdentity,
      opened.Record.OriginalExecutableIdentity);
    Assert.Equal(created.Layout.ManagedRoot.Path, binding!.ManagedRootPath);
    Assert.True(File.Exists(opened.Layout.StableLauncherPath));
    Assert.Throws<OwnedDirectoryException>(() => opened.Directories.CreateNamedExclusive(
      opened.Layout.Staging, "x", "payload", opened.Layout.InstallationId, Guid.NewGuid()));
  }

  [Fact]
  public void CreateWithoutLauncherPayloadLeavesNothingBehind()
  {
    File.Delete(Path.Combine(Original, "BookSplice.Launcher.exe"));
    Assert.Throws<InvalidDataException>(() => InstallationOpener.Create(Original, Data, "0.1.0", Programs));
    Assert.Empty(Directory.GetDirectories(_root, ".booksplice-managed-*"));
    Assert.Null(InstallationRegistry.Open(Data, create: false));
  }

  [Fact]
  public void ReplacedLauncherIsNeverStarted()
  {
    var created = InstallationOpener.Create(Original, Data, "0.1.0", Programs);
    File.AppendAllText(created.Layout.StableLauncherPath, "x");
    Assert.Throws<InvalidDataException>(() => StableLauncherBootstrap.Start(created.Layout, created.Record, []));
  }

  [Fact]
  public void ShortcutIsCreatedAfterCommitAndRepeatedRunsLeaveItAlone()
  {
    var created = InstallationOpener.Create(Original, Data, "0.1.0", Programs);
    Assert.Null(CommittedFollowUp.Run(created, Programs));
    var shortcut = Path.Combine(Programs, "BookSplice.lnk");
    Assert.Equal(created.Layout.StableLauncherPath,
      ManagedShortcutStore.InspectLink(shortcut).Target, ignoreCase: true);
    var bytes = File.ReadAllBytes(shortcut);
    Assert.Null(CommittedFollowUp.Run(created, Programs));
    Assert.Equal(bytes, File.ReadAllBytes(shortcut));
  }

  [Fact]
  public void UnownedShortcutCollisionIsReportedAndKept()
  {
    var created = InstallationOpener.Create(Original, Data, "0.1.0", Programs);
    var shortcut = Path.Combine(Programs, "BookSplice.lnk");
    File.WriteAllText(shortcut, "someone else");
    var message = CommittedFollowUp.Run(created, Programs);
    Assert.Contains(created.Layout.StableLauncherPath, message);
    Assert.Equal("someone else", File.ReadAllText(shortcut));
  }
}
