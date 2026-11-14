using System.Security.Cryptography;
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
  public void SecondCreateForTheSameExecutableLeavesNoOrphan()
  {
    InstallationOpener.Create(Original, Data, "0.1.0", Programs);
    Assert.Throws<InvalidDataException>(() => InstallationOpener.Create(Original, Data, "0.1.0", Programs));
    Assert.Single(Directory.GetDirectories(_root, ".booksplice-managed-*"));
  }

  [Fact]
  public void ManagedCopyCannotCreateANestedInstallation()
  {
    var created = InstallationOpener.Create(Original, Data, "0.1.0", Programs);
    var inside = Path.Combine(created.Layout.Versions.Path, "0.1.0-copy");
    OriginalPackageFixture.Write(inside, "0.1.0");
    Assert.Throws<InvalidDataException>(() => InstallationOpener.Create(inside, Data, "0.1.0", Programs));
    Assert.Single(Directory.GetDirectories(_root, ".booksplice-managed-*"));
    Assert.Empty(Directory.GetDirectories(created.Layout.Versions.Path, ".booksplice-managed-*"));
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

  // Puts an older launcher in place and records it, as an installation made by an earlier release has.
  private static OpenedInstallation WithOlderLauncher(OpenedInstallation created)
  {
    var older = Enumerable.Repeat((byte)'o', 4096).ToArray();
    File.WriteAllBytes(created.Layout.StableLauncherPath, older);
    created.State.Transition<InstallationRecord>(Path.Combine(created.RecordsPath, "installation.json"),
      read => read.Value! with { StableLauncherSha256 = Convert.ToHexStringLower(SHA256.HashData(older)) });
    return created;
  }

  private byte[] PackagedLauncher => File.ReadAllBytes(Path.Combine(Original, "BookSplice.Launcher.exe"));

  [Fact]
  public void RefreshReplacesAnOlderRecordedLauncherWithThePackagedOne()
  {
    var created = WithOlderLauncher(InstallationOpener.Create(Original, Data, "0.1.0", Programs));
    var opened = InstallationOpener.Open(created.Layout.ManagedRoot.Path, programsDirectory: Programs);
    Assert.Equal(LauncherRefreshOutcome.Replaced, StableLauncherBootstrap.Refresh(opened));
    Assert.Equal(PackagedLauncher, File.ReadAllBytes(created.Layout.StableLauncherPath));
    Assert.False(File.Exists(created.Layout.StableLauncherPath + ".new"));
    var reopened = InstallationOpener.Open(created.Layout.ManagedRoot.Path, programsDirectory: Programs);
    Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(PackagedLauncher)),
      reopened.Record.StableLauncherSha256, ignoreCase: true);
    Assert.Equal(LauncherRefreshOutcome.Current, StableLauncherBootstrap.Refresh(reopened));
  }

  [Fact]
  public void TheRunningLauncherIsCheckedByIdentityWithoutHashing()
  {
    var created = InstallationOpener.Create(Original, Data, "0.1.0", Programs);
    var bytes = File.ReadAllBytes(created.Layout.StableLauncherPath);
    bytes[^1] ^= 0xFF;
    File.WriteAllBytes(created.Layout.StableLauncherPath, bytes);
    _ = InstallationOpener.Open(created.Layout.ManagedRoot.Path, programsDirectory: Programs,
      imageIsRunning: true);
    Assert.Throws<InvalidDataException>(() =>
      InstallationOpener.Open(created.Layout.ManagedRoot.Path, programsDirectory: Programs));
  }

  [Fact]
  public void RefreshLeavesTheCurrentLauncherAlone()
  {
    var created = InstallationOpener.Create(Original, Data, "0.1.0", Programs);
    // A current launcher is settled without the update admission, so it never blocks CLI jobs.
    using (var update = new ActivityLeaseStore(created.Layout.DataRoot).TryEnterUpdate())
    {
      Assert.NotNull(update);
      Assert.Equal(LauncherRefreshOutcome.Current, StableLauncherBootstrap.Refresh(created));
    }
    Assert.Equal(created.Record.StableLauncherIdentity,
      InstallationOpener.Open(created.Layout.ManagedRoot.Path, programsDirectory: Programs)
        .Record.StableLauncherIdentity);
  }

  [Fact]
  public void RefreshRefusesAnUnrecordedLauncherImage()
  {
    var created = WithOlderLauncher(InstallationOpener.Create(Original, Data, "0.1.0", Programs));
    File.AppendAllText(created.Layout.StableLauncherPath, "x");
    Assert.Throws<InvalidDataException>(() => StableLauncherBootstrap.Refresh(created));
    Assert.EndsWith("x", File.ReadAllText(created.Layout.StableLauncherPath));
  }

  [Fact]
  public void RefreshWaitsWhileTheLauncherIsInUse()
  {
    var created = WithOlderLauncher(InstallationOpener.Create(Original, Data, "0.1.0", Programs));
    var before = created.State.Read<InstallationRecord>(
      Path.Combine(created.RecordsPath, "installation.json")).Value!;
    using (File.Open(created.Layout.StableLauncherPath, FileMode.Open, FileAccess.Read, FileShare.Read))
      Assert.Equal(LauncherRefreshOutcome.Busy, StableLauncherBootstrap.Refresh(created));
    Assert.False(File.Exists(created.Layout.StableLauncherPath + ".new"));
    var after = created.State.Read<InstallationRecord>(
      Path.Combine(created.RecordsPath, "installation.json")).Value!;
    Assert.Equal(before.StableLauncherIdentity, after.StableLauncherIdentity);
    Assert.Equal(before.StableLauncherSha256, after.StableLauncherSha256);
    Assert.Equal(LauncherRefreshOutcome.Replaced, StableLauncherBootstrap.Refresh(created));
  }

  [Fact]
  public void OpenFinishesARefreshThatStoppedBeforeRecording()
  {
    var created = WithOlderLauncher(InstallationOpener.Create(Original, Data, "0.1.0", Programs));
    File.Delete(created.Layout.StableLauncherPath);
    File.WriteAllBytes(created.Layout.StableLauncherPath, PackagedLauncher);
    var opened = InstallationOpener.Open(created.Layout.ManagedRoot.Path, programsDirectory: Programs);
    Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(PackagedLauncher)),
      opened.Record.StableLauncherSha256, ignoreCase: true);
    using (var image = File.OpenHandle(created.Layout.StableLauncherPath))
      Assert.Equal(NativeFileIdentity.Read(image), opened.Record.StableLauncherIdentity);
    StableLauncherBootstrap.RequireRecorded(opened.Layout, opened.Record);
  }

  [Fact]
  public void OpenRefusesAReplacedLauncherThatIsNotThePackagedOne()
  {
    var created = InstallationOpener.Create(Original, Data, "0.1.0", Programs);
    File.AppendAllText(created.Layout.StableLauncherPath, "x");
    Assert.Throws<InvalidDataException>(() =>
      InstallationOpener.Open(created.Layout.ManagedRoot.Path, programsDirectory: Programs));
  }
}
