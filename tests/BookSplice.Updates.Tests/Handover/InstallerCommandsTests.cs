using BookSplice.Updates.Handover;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Tests.Storage;
using BookSplice.Updates.Windows;
using Microsoft.Win32;

namespace BookSplice.Updates.Tests.Handover;

[Collection("WindowsOwnedStorage")]
public sealed class InstallerCommandsTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(),
    "booksplice-install-" + Guid.NewGuid().ToString("N"));
  private string Original => Path.Combine(_root, "app");
  private string Data => Path.Combine(_root, "data");
  private string Programs => Path.Combine(_root, "Programs");
  private string Shortcut => Path.Combine(Programs, "BookSplice.lnk");

  public InstallerCommandsTests()
  {
    Directory.CreateDirectory(Data);
    Directory.CreateDirectory(Programs);
    OriginalPackageFixture.Write(Original, "0.1.0");
  }

  public void Dispose() => Directory.Delete(_root, true);

  private int Register(bool replace = false, StringWriter? error = null, string? original = null) =>
    InstallerCommands.Register(original ?? Original, Data, replace, error ?? new StringWriter(), Programs);

  private string[] ManagedRoots() => Directory.GetDirectories(_root, ".booksplice-managed-*");

  private IReadOnlyList<InstallationBinding> Bindings() =>
    InstallationRegistry.Open(Data, create: false)!.Read().Value!.Bindings;

  [Fact]
  public void RegisterOnAFreshFolderCreatesTheInstallationAndShortcut()
  {
    Assert.Equal(InstallerCommands.Done, Register());
    var managed = Assert.Single(ManagedRoots());
    var opened = InstallationOpener.Open(managed, programsDirectory: Programs);
    var current = opened.State.Read<CurrentTarget>(Path.Combine(opened.RecordsPath, "current.json"));
    Assert.Equal(TargetKind.Original, current.Value!.Current.Kind);
    Assert.Equal("0.1.0", opened.Record.OriginalVersion);
    var binding = Assert.Single(Bindings());
    Assert.Equal(Path.Combine(Original, "BookSplice.Gui.exe"), binding.ExecutablePath, ignoreCase: true);
    Assert.Equal(managed, binding.ManagedRootPath, ignoreCase: true);
    Assert.Equal(opened.Layout.StableLauncherPath, ManagedShortcutStore.InspectLink(Shortcut).Target,
      ignoreCase: true);
  }

  [Fact]
  public void RegisterWorksWhenTheDataRootDoesNotExistYet()
  {
    var fresh = Path.Combine(_root, "fresh", "BookSplice");
    Directory.CreateDirectory(Path.GetDirectoryName(fresh)!);
    var error = new StringWriter();
    var code = InstallerCommands.Register(Original, fresh, false, error, Programs);
    Assert.True(code == InstallerCommands.Done, error.ToString());
    Assert.Single(InstallationRegistry.Open(fresh, create: false)!.Read().Value!.Bindings);
  }

  [Fact]
  public void UnregisterUnbindsFromTheRecordedDataRootNotTheLeaseRoot()
  {
    Assert.Equal(InstallerCommands.Done, Register());
    var managed = Assert.Single(ManagedRoots());
    var other = Path.Combine(_root, "other-data");
    Directory.CreateDirectory(other);
    var toastKey = @"Software\BookSplice.Tests\" + Guid.NewGuid().ToString("N");
    try
    {
      Assert.Equal(InstallerCommands.Done,
        InstallerCommands.Unregister(managed, other, new StringWriter(), Programs, toastKey));
      Assert.Empty(Bindings());
      Assert.Null(InstallationRegistry.Open(other, create: false));
    }
    finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\BookSplice.Tests", false); }
  }

  [Fact]
  public void InstalledIsTrueOnlyForABoundFolder()
  {
    Assert.Equal(InstallerCommands.Failed, InstallerCommands.IsInstalled(Original, Data));
    Assert.Equal(InstallerCommands.Done, Register());
    Assert.Equal(InstallerCommands.Done, InstallerCommands.IsInstalled(Original, Data));
    Assert.Equal(InstallerCommands.Failed,
      InstallerCommands.IsInstalled(Path.Combine(_root, "elsewhere"), Data));
    Assert.Equal(InstallerCommands.Failed, InstallerCommands.IsInstalled(Original, Path.Combine(_root, "none")));
    File.WriteAllText(Path.Combine(Data, "updates", "registry.json"), "not json");
    Assert.Equal(InstallerCommands.Unknown, InstallerCommands.IsInstalled(Original, Data));
  }

  [Fact]
  public void RegisterTwiceIsIdempotent()
  {
    Assert.Equal(InstallerCommands.Done, Register());
    var link = File.ReadAllBytes(Shortcut);
    Assert.Equal(InstallerCommands.Done, Register());
    Assert.Single(ManagedRoots());
    Assert.Single(Bindings());
    Assert.Equal(link, File.ReadAllBytes(Shortcut));
  }

  [Fact]
  public void AnUnownedShortcutIsALeftAloneCollisionUnlessReplaced()
  {
    File.WriteAllText(Shortcut, "unowned");
    var error = new StringWriter();
    Assert.Equal(InstallerCommands.ShortcutCollision, Register(error: error));
    Assert.Contains("left alone", error.ToString());
    Assert.Equal("unowned", File.ReadAllText(Shortcut));
    Assert.Single(Bindings());
    Assert.Equal(InstallerCommands.Done, Register(replace: true));
    Assert.Single(ManagedRoots());
    Assert.Equal(InstallationOpener.Open(ManagedRoots()[0], programsDirectory: Programs).Layout.StableLauncherPath,
      ManagedShortcutStore.InspectLink(Shortcut).Target, ignoreCase: true);
  }

  [Fact]
  public void ReplaceNeverDeletesADirectoryNamedLikeTheShortcut()
  {
    Directory.CreateDirectory(Shortcut);
    File.WriteAllText(Path.Combine(Shortcut, "keep.txt"), "x");
    Assert.Equal(InstallerCommands.ShortcutCollision, Register(replace: true));
    Assert.True(File.Exists(Path.Combine(Shortcut, "keep.txt")));
  }

  [Fact]
  public void AStaleUnboundManagedRootDoesNotBlockANewRegister()
  {
    var stale = Path.Combine(_root, ".booksplice-managed-" + Guid.NewGuid().ToString("D"));
    Directory.CreateDirectory(Path.Combine(stale, "records"));
    File.WriteAllText(Path.Combine(stale, "records", "installation.json"), "{");
    Assert.Equal(InstallerCommands.Done, Register());
    Assert.Equal(2, ManagedRoots().Length);
    Assert.Single(Bindings());
    Assert.True(Directory.Exists(stale));
  }

  [Fact]
  public void ABindingWhoseManagedRootIsGoneIsReplacedByANewInstallation()
  {
    Assert.Equal(InstallerCommands.Done, Register());
    var oldRoot = Assert.Single(ManagedRoots());
    Directory.Delete(oldRoot, true);
    var error = new StringWriter();
    Assert.True(InstallerCommands.Done == Register(replace: true, error: error), error.ToString());
    var binding = Assert.Single(Bindings());
    Assert.NotEqual(oldRoot, binding.ManagedRootPath, StringComparer.OrdinalIgnoreCase);
    Assert.Equal(Assert.Single(ManagedRoots()), binding.ManagedRootPath, ignoreCase: true);
  }

  [Fact]
  public void RegisterFailsWithoutAVersionOrGuiExecutable()
  {
    File.Delete(Path.Combine(Original, "VERSION.txt"));
    var error = new StringWriter();
    Assert.Equal(InstallerCommands.Failed, Register(error: error));
    Assert.StartsWith("Register failed:", error.ToString());
    Assert.Empty(ManagedRoots());
  }

  [Fact]
  public void BothCommandsRefuseWhileTheGuiHoldsItsLease()
  {
    Assert.Equal(InstallerCommands.Done, Register());
    var managed = Assert.Single(ManagedRoots());
    using (GuiInstanceLease.TryAcquire(Data))
    {
      var error = new StringWriter();
      Assert.Equal(InstallerCommands.Busy, Register(error: error));
      Assert.Contains("running", error.ToString());
      Assert.Equal(InstallerCommands.Busy,
        InstallerCommands.Unregister(managed, Data, new StringWriter(), Programs));
    }
    Assert.Single(Bindings());
    Assert.True(File.Exists(Shortcut));
  }

  [Fact]
  public void BothCommandsRefuseWhileAnUpdateOrJobIsActive()
  {
    Assert.Equal(InstallerCommands.Done, Register());
    var managed = Assert.Single(ManagedRoots());
    using (new ActivityLeaseStore(Data).TryEnterJob())
    {
      Assert.Equal(InstallerCommands.Busy, Register());
      Assert.Equal(InstallerCommands.Busy,
        InstallerCommands.Unregister(managed, Data, new StringWriter(), Programs));
    }
    Assert.Single(Bindings());
  }

  [Fact]
  public void UnregisterRemovesTheOwnedShortcutBindingAndToastKeyOnly()
  {
    var toastKey = @"Software\BookSplice.Tests\" + Guid.NewGuid().ToString("N");
    using (var key = Registry.CurrentUser.CreateSubKey(toastKey)) key.SetValue("DisplayName", "x");
    try
    {
      Assert.Equal(InstallerCommands.Done, Register());
      var managed = Assert.Single(ManagedRoots());
      File.WriteAllText(Path.Combine(Data, "settings.json"), "{}");
      Assert.Equal(InstallerCommands.Done,
        InstallerCommands.Unregister(managed, Data, new StringWriter(), Programs, toastKey));
      Assert.False(File.Exists(Shortcut));
      Assert.Empty(Bindings());
      Assert.Null(Registry.CurrentUser.OpenSubKey(toastKey));
      Assert.True(Directory.Exists(managed));
      Assert.True(File.Exists(Path.Combine(Original, "BookSplice.Gui.exe")));
      Assert.True(File.Exists(Path.Combine(Data, "settings.json")));
      // A second run after the bindings are gone still succeeds.
      var again = new StringWriter();
      Assert.True(InstallerCommands.Done == InstallerCommands.Unregister(managed, Data, again, Programs, toastKey), again.ToString());
    }
    finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\BookSplice.Tests", false); }
  }

  [Fact]
  public void UnregisterLeavesAShortcutThatIsNotOwnedOrWasChanged()
  {
    var toastKey = @"Software\BookSplice.Tests\" + Guid.NewGuid().ToString("N");
    try
    {
      File.WriteAllText(Shortcut, "unowned");
      Assert.Equal(InstallerCommands.ShortcutCollision, Register());
      var managed = Assert.Single(ManagedRoots());
      Assert.Equal(InstallerCommands.Done,
        InstallerCommands.Unregister(managed, Data, new StringWriter(), Programs, toastKey));
      Assert.Equal("unowned", File.ReadAllText(Shortcut));
      Assert.Empty(Bindings());

      File.Delete(Shortcut);
      Assert.Equal(InstallerCommands.Done, Register());
      managed = ManagedRoots().Single(root => Bindings().Any(binding =>
        string.Equals(binding.ManagedRootPath, root, StringComparison.OrdinalIgnoreCase)));
      File.AppendAllText(Shortcut, "x");
      Assert.Equal(InstallerCommands.Done,
        InstallerCommands.Unregister(managed, Data, new StringWriter(), Programs, toastKey));
      Assert.True(File.Exists(Shortcut));
      Assert.Empty(Bindings());
    }
    finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\BookSplice.Tests", false); }
  }

  [Fact]
  public void UnbindRemovesOnlyThatInstallationsBindings()
  {
    var second = Path.Combine(_root, "other", "app");
    OriginalPackageFixture.Write(second, "0.1.0");
    var first = InstallationOpener.Create(Original, Data, "0.1.0", Programs);
    var other = InstallationOpener.Create(second, Data, "0.1.0", Programs);
    var registry = InstallationRegistry.Open(Data, create: false)!;
    registry.Unbind(first.Layout.InstallationId);
    var remaining = Assert.Single(registry.Read().Value!.Bindings);
    Assert.Equal(other.Layout.InstallationId, remaining.InstallationId);
    registry.Unbind(first.Layout.InstallationId);
    Assert.Single(registry.Read().Value!.Bindings);
    registry.Unbind(other.Layout.InstallationId);
    Assert.Empty(registry.Read().Value!.Bindings);
  }

  [Fact]
  public void UnbindLeavesAMissingRegistryMissingAndRejectsAnInvalidOne()
  {
    Assert.Null(InstallationRegistry.Open(Data, create: false));
    var created = InstallationRegistry.Open(Data, create: true)!;
    created.Unbind(Guid.NewGuid());
    Assert.Equal(StateReadKind.Missing, created.Read().Kind);
    var path = Path.Combine(Data, "updates", "registry.json");
    File.WriteAllText(path, "not json");
    Assert.Throws<InvalidDataException>(() => created.Unbind(Guid.NewGuid()));
    Assert.Equal("not json", File.ReadAllText(path));
  }
}
