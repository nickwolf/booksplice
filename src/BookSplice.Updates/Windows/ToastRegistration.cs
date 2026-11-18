using System.Runtime.Versioning;
using Microsoft.Win32;

namespace BookSplice.Updates.Windows;

// Registry side of toast identity under a classes root (HKCU\Software\Classes in production).
[SupportedOSPlatform("windows")]
public sealed class ToastRegistration(RegistryKey classesRoot, string appUserModelId, Guid clsid)
{
  public const string ActivatedArgument = "--toast-activated";

  private string AumidPath => @"AppUserModelId\" + appUserModelId;
  private string ClsidPath => @"CLSID\" + clsid.ToString("B");
  private string ServerPath => ClsidPath + @"\LocalServer32";

  public static ToastRegistration ForCurrentUser(ToastIdentity identity) =>
    new(Registry.CurrentUser.CreateSubKey(@"Software\Classes", true), identity.AppUserModelId, identity.ActivatorClsid);

  public void Register(string displayName, string? iconPath)
  {
    using var key = classesRoot.CreateSubKey(AumidPath, true);
    key.SetValue("DisplayName", displayName);
    if (iconPath is { Length: > 0 }) key.SetValue("IconUri", iconPath);
    else key.DeleteValue("IconUri", false);
  }

  public void RegisterActivator(string launcherPath)
  {
    if (!Path.IsPathFullyQualified(launcherPath))
      throw new ArgumentException("The launcher path must be fully qualified.", nameof(launcherPath));
    using (var key = classesRoot.CreateSubKey(AumidPath, true))
      key.SetValue("CustomActivator", clsid.ToString("B"));
    using var server = classesRoot.CreateSubKey(ServerPath, true);
    server.SetValue(null, $"\"{launcherPath}\" {ActivatedArgument}");
  }

  public void RemoveActivator()
  {
    using (var key = classesRoot.OpenSubKey(AumidPath, true)) key?.DeleteValue("CustomActivator", false);
    classesRoot.DeleteSubKeyTree(ClsidPath, false);
  }

  public void Unregister()
  {
    classesRoot.DeleteSubKeyTree(AumidPath, false);
    classesRoot.DeleteSubKeyTree(ClsidPath, false);
  }

  public bool RemoveActivatorIfOwnedBy(string launcherPath)
  {
    string owner;
    using (var server = classesRoot.OpenSubKey(ServerPath))
      if (server?.GetValue(null) is not string command || CommandPath(command) is not { } path) return false;
      else owner = path;
    if (!string.Equals(Path.GetFullPath(owner), Path.GetFullPath(launcherPath), StringComparison.OrdinalIgnoreCase))
      return false;
    RemoveActivator();
    return true;
  }

  private static string? CommandPath(string command)
  {
    if (command.Length < 2 || command[0] != '"') return null;
    var end = command.IndexOf('"', 1);
    return end > 1 && Path.IsPathFullyQualified(command[1..end]) ? command[1..end] : null;
  }
}
