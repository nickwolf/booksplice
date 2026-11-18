using System.Diagnostics;
using System.IO;
using System.Security;
using BookSplice.Updates.Windows;

namespace BookSplice.Gui.Notifications;

// Keeps the toast registry keys in line with the notifications setting. The click activator is written only
// once this version is committed and the stable launcher is its own copy, so the keys never name a launcher
// that doesn't understand them; until then, and before a rollback, it is removed.
public sealed class ToastRegistrar(ToastRegistration registration, string? defaultIcon)
{
  private readonly object _gate = new();
  private bool _enabled;
  private string? _launcher;

  public void Sync(bool enabled)
  {
    lock (_gate)
    {
      _enabled = enabled;
      Apply();
    }
  }

  public void UseLauncher(string? launcherPath)
  {
    lock (_gate)
    {
      _launcher = launcherPath;
      Apply();
    }
  }

  public void Ensure()
  {
    lock (_gate) if (_enabled) Apply();
  }

  private void Apply()
  {
    try
    {
      if (!_enabled)
      {
        registration.Unregister();
        return;
      }
      registration.Register("BookSplice", _launcher ?? defaultIcon);
      if (_launcher is { } launcher) registration.RegisterActivator(launcher);
      else registration.RemoveActivator();
    }
    catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
    {
      Trace.TraceWarning($"Notification registration failed: {error.Message}");
    }
  }
}
