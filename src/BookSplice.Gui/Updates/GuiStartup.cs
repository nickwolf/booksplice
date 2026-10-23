using System.IO;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Gui.Updates;

public sealed record LaunchedImage(string Path, FileIdentity Identity);

// Normal startup: forward to the committed version when one is mapped, then take the instance lease.
public static class GuiStartup
{
  // Returns true when the stable launcher was started and this process should exit without a window.
  // The launcher starts exactly the mapped image, which then finds itself mapped, so there is no loop.
  public static bool TryForward(LaunchedImage self, Func<LaunchedImage?> mapped, Action startLauncher)
  {
    try
    {
      if (mapped() is not { } target ||
          (target.Identity == self.Identity &&
           string.Equals(target.Path, self.Path, StringComparison.OrdinalIgnoreCase)))
        return false;
      startLauncher();
      return true;
    }
    catch (Exception error) when (error is IOException or InvalidDataException or
      UnauthorizedAccessException or InvalidOperationException or ArgumentException or
      System.ComponentModel.Win32Exception)
    {
      return false;
    }
  }

  public static GuiInstanceLease? AcquireLease(string dataRoot, Action<string> notify)
  {
    var lease = GuiInstanceLease.TryAcquire(dataRoot);
    if (lease is null) notify("BookSplice is already running.");
    return lease;
  }
}
