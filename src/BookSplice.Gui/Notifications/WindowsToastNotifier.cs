using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace BookSplice.Gui.Notifications;

// Shows toasts through the Windows SDK projection. Only an AppUserModelId key is registered: no CLSID or COM activator,
// so a click while BookSplice is closed does nothing and nothing in the registry points at a versioned folder.
public sealed class WindowsToastNotifier(string? iconPath = null, Action<string?>? activated = null) : IConversionNotifier
{
  public const string AppUserModelId = "BookSplice.App";
  private const string KeyPath = @"Software\Classes\AppUserModelId\" + AppUserModelId;

  public static void Register(string? iconPath)
  {
    using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
    key.SetValue("DisplayName", "BookSplice");
    if (iconPath is { Length: > 0 }) key.SetValue("IconUri", iconPath);
    else key.DeleteValue("IconUri", false);
  }

  public static void Unregister() => Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);

  public static void Sync(bool enabled, string? iconPath)
  {
    try { if (enabled) Register(iconPath); else Unregister(); }
    catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
    { Trace.TraceWarning($"Notification registration failed: {error.Message}"); }
  }

  public void Show(NotificationRequest request)
  {
    Register(iconPath);
    var xml = new XmlDocument();
    xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\"><text/><text/></binding></visual></toast>");
    var texts = xml.GetElementsByTagName("text");
    texts[0].InnerText = request.Title;
    texts[1].InnerText = request.Body;
    var toast = new ToastNotification(xml);
    if (activated is not null) toast.Activated += (_, _) => activated(request.ResultKey);
    ToastNotificationManager.CreateToastNotifier(AppUserModelId).Show(toast);
  }
}
