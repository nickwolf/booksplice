using BookSplice.Updates.Windows;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace BookSplice.Gui.Notifications;

// Shows toasts through the Windows SDK projection. The registrar owns the registry keys; without one (source runs
// and tests) nothing is registered. A click while the pop-up is up arrives through Activated; one from the
// notification list arrives through the activator, as the toast's launch argument. That argument is a random
// token rather than the book's path, because Windows keeps it in its notification history.
public sealed class WindowsToastNotifier(Action<string?>? activated = null) : IConversionNotifier
{
  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _keys = new(StringComparer.Ordinal);

  public ToastRegistrar? Registrar { get; set; }

  // The queue key for a token this process issued, or null for an unknown one (from an earlier run).
  public string? Resolve(string? token) => token is not null && _keys.TryGetValue(token, out var key) ? key : null;

  public void Show(NotificationRequest request)
  {
    Registrar?.Ensure();
    var xml = new XmlDocument();
    xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\"><text/><text/></binding></visual></toast>");
    if (request.ResultKey is { } key)
    {
      var token = Guid.NewGuid().ToString("N");
      _keys[token] = key;
      xml.DocumentElement.SetAttribute("launch", token);
    }
    var texts = xml.GetElementsByTagName("text");
    texts[0].InnerText = request.Title;
    texts[1].InnerText = request.Body;
    var toast = new ToastNotification(xml);
    if (activated is not null) toast.Activated += (_, _) => activated(request.ResultKey);
    ToastNotificationManager.CreateToastNotifier(ToastIdentity.Current.AppUserModelId).Show(toast);
  }
}
