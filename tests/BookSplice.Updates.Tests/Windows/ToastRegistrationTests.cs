using System.Runtime.Versioning;
using BookSplice.Updates.Windows;
using Microsoft.Win32;

namespace BookSplice.Updates.Tests.Windows;

[SupportedOSPlatform("windows")]
public sealed class ToastRegistrationTests : IDisposable
{
  private const string Aumid = "BookSplice.Scratch.App";
  private static readonly Guid Clsid = Guid.NewGuid();
  private readonly string _scratchPath = @"Software\BookSplice.Tests\" + Guid.NewGuid().ToString("N");
  private readonly RegistryKey _root;
  private readonly ToastRegistration _registration;

  public ToastRegistrationTests()
  {
    _root = Registry.CurrentUser.CreateSubKey(_scratchPath, true);
    _registration = new ToastRegistration(_root, Aumid, Clsid);
  }

  public void Dispose()
  {
    _root.Dispose();
    Registry.CurrentUser.DeleteSubKeyTree(_scratchPath, false);
  }

  private string? AumidValue(string name)
  {
    using var key = _root.OpenSubKey(@"AppUserModelId\" + Aumid);
    return key?.GetValue(name) as string;
  }

  private string? ServerCommand()
  {
    using var key = _root.OpenSubKey(@"CLSID\" + Clsid.ToString("B") + @"\LocalServer32");
    return key?.GetValue(null) as string;
  }

  private bool ClsidExists()
  {
    using var key = _root.OpenSubKey(@"CLSID\" + Clsid.ToString("B"));
    return key is not null;
  }

  [Fact]
  public void RegisterSetsAndClearsIcon()
  {
    _registration.Register("BookSplice", @"C:\icons\a.ico");
    Assert.Equal("BookSplice", AumidValue("DisplayName"));
    Assert.Equal(@"C:\icons\a.ico", AumidValue("IconUri"));
    _registration.Register("BookSplice", null);
    Assert.Null(AumidValue("IconUri"));
    _registration.Register("BookSplice", @"C:\icons\a.ico");
    _registration.Register("BookSplice", "");
    Assert.Null(AumidValue("IconUri"));
  }

  [Fact]
  public void RegisterActivatorWritesBothValues()
  {
    _registration.RegisterActivator(@"C:\Apps\Book Splice\BookSplice.exe");
    Assert.Equal(Clsid.ToString("B"), AumidValue("CustomActivator"));
    Assert.Equal("\"C:\\Apps\\Book Splice\\BookSplice.exe\" --toast-activated", ServerCommand());
  }

  [Fact]
  public void RegisterActivatorRejectsRelativePath() =>
    Assert.Throws<ArgumentException>(() => _registration.RegisterActivator(@"BookSplice.exe"));

  [Fact]
  public void RemoveActivatorKeepsIdentity()
  {
    _registration.Register("BookSplice", null);
    _registration.RegisterActivator(@"C:\Apps\BookSplice.exe");
    _registration.RemoveActivator();
    Assert.Null(AumidValue("CustomActivator"));
    Assert.Equal("BookSplice", AumidValue("DisplayName"));
    Assert.False(ClsidExists());
  }

  [Fact]
  public void UnregisterRemovesBothTrees()
  {
    _registration.Register("BookSplice", null);
    _registration.RegisterActivator(@"C:\Apps\BookSplice.exe");
    _registration.Unregister();
    using var aumid = _root.OpenSubKey(@"AppUserModelId\" + Aumid);
    Assert.Null(aumid);
    Assert.False(ClsidExists());
  }

  [Fact]
  public void RemoveIfOwnedByMatchesPathIgnoringCase()
  {
    _registration.Register("BookSplice", null);
    _registration.RegisterActivator(@"C:\Apps\BookSplice.exe");
    Assert.True(_registration.RemoveActivatorIfOwnedBy(@"c:\apps\booksplice.EXE"));
    Assert.Null(AumidValue("CustomActivator"));
    Assert.False(ClsidExists());
    Assert.Equal("BookSplice", AumidValue("DisplayName"));
  }

  [Fact]
  public void RemoveIfOwnedByLeavesAnotherOwnersActivator()
  {
    _registration.RegisterActivator(@"C:\Apps\BookSplice.exe");
    Assert.False(_registration.RemoveActivatorIfOwnedBy(@"D:\Portable\BookSplice.exe"));
    Assert.Equal(Clsid.ToString("B"), AumidValue("CustomActivator"));
    Assert.Equal("\"C:\\Apps\\BookSplice.exe\" --toast-activated", ServerCommand());
  }

  [Fact]
  public void MissingKeysAreNotErrors()
  {
    _registration.RemoveActivator();
    _registration.Unregister();
    Assert.False(_registration.RemoveActivatorIfOwnedBy(@"C:\Apps\BookSplice.exe"));
  }

  [Fact]
  public void IdentitiesAreSeparatedByFlavor()
  {
    Assert.Equal("BookSplice.App", ToastIdentity.For(BuildFlavor.Release).AppUserModelId);
    Assert.Equal("BookSplice.Test.App", ToastIdentity.For(BuildFlavor.Test).AppUserModelId);
    Assert.NotEqual(ToastIdentity.Release.ActivatorClsid, ToastIdentity.Test.ActivatorClsid);
  }
}
