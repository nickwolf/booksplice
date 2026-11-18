using System.Runtime.Versioning;
using BookSplice.Gui.Notifications;
using BookSplice.Updates.Windows;
using Microsoft.Win32;

namespace BookSplice.Gui.Tests;

[SupportedOSPlatform("windows")]
public sealed class ToastRegistrarTests : IDisposable
{
  private const string Aumid = "BookSplice.Scratch.App";
  private static readonly Guid Clsid = Guid.NewGuid();
  private readonly string _scratchPath = @"Software\BookSplice.Tests\" + Guid.NewGuid().ToString("N");
  private readonly RegistryKey _root;
  private readonly ToastRegistrar _registrar;

  public ToastRegistrarTests()
  {
    _root = Registry.CurrentUser.CreateSubKey(_scratchPath, true);
    _registrar = new ToastRegistrar(new ToastRegistration(_root, Aumid, Clsid), @"C:\app\0.6.0\BookSplice.Gui.exe");
  }

  public void Dispose()
  {
    _root.Dispose();
    Registry.CurrentUser.DeleteSubKeyTree(_scratchPath, false);
  }

  private string? Value(string name)
  {
    using var key = _root.OpenSubKey(@"AppUserModelId\" + Aumid);
    return key?.GetValue(name) as string;
  }

  private bool HasServer()
  {
    using var key = _root.OpenSubKey(@"CLSID\" + Clsid.ToString("B"));
    return key is not null;
  }

  [Fact]
  public void WithoutAConfirmedLauncherOnlyTheIdentityIsRegistered()
  {
    _registrar.Sync(true);
    Assert.Equal("BookSplice", Value("DisplayName"));
    Assert.Equal(@"C:\app\0.6.0\BookSplice.Gui.exe", Value("IconUri"));
    Assert.Null(Value("CustomActivator"));
    Assert.False(HasServer());
  }

  [Fact]
  public void AConfirmedLauncherAddsTheActivatorAndBecomesTheIcon()
  {
    _registrar.Sync(true);
    _registrar.UseLauncher(@"C:\app\.booksplice-managed-x\BookSplice.Launcher.exe");
    Assert.Equal(Clsid.ToString("B"), Value("CustomActivator"));
    Assert.Equal(@"C:\app\.booksplice-managed-x\BookSplice.Launcher.exe", Value("IconUri"));
    Assert.True(HasServer());
  }

  [Fact]
  public void ClearingTheLauncherBeforeARollbackRemovesOnlyTheActivator()
  {
    _registrar.Sync(true);
    _registrar.UseLauncher(@"C:\app\.booksplice-managed-x\BookSplice.Launcher.exe");
    _registrar.UseLauncher(null);
    Assert.Equal("BookSplice", Value("DisplayName"));
    Assert.Null(Value("CustomActivator"));
    Assert.False(HasServer());
  }

  [Fact]
  public void TurningNotificationsOffRemovesEverythingAndOnRestoresTheActivator()
  {
    _registrar.Sync(true);
    _registrar.UseLauncher(@"C:\app\.booksplice-managed-x\BookSplice.Launcher.exe");
    _registrar.Sync(false);
    Assert.Null(Value("DisplayName"));
    Assert.False(HasServer());
    _registrar.Ensure();
    Assert.Null(Value("DisplayName"));
    _registrar.Sync(true);
    Assert.Equal(Clsid.ToString("B"), Value("CustomActivator"));
    Assert.True(HasServer());
  }
}
