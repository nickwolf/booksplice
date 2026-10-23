using System.Windows;

namespace BookSplice.Gui.Diagnostics;

// Never goes through Application.Shutdown: that would run the main window's closing prompt and leave conversions running.
public sealed class WpfCrashUi(ActiveJobRegistry jobs, TimeSpan? waitBound = null, Action<int>? exit = null) : ICrashUi
{
  public static readonly TimeSpan DefaultWaitBound = TimeSpan.FromSeconds(3);

  public void ShowFatal(bool reportSaved) =>
    MessageBox.Show(reportSaved ? CrashMessages.Saved : CrashMessages.NotSaved, "BookSplice", MessageBoxButton.OK, MessageBoxImage.Error);

  public void Shutdown(int exitCode)
  {
    try
    {
      jobs.CancelAll();
      jobs.WaitForAll(waitBound ?? DefaultWaitBound);
    }
    catch { }
    (exit ?? Environment.Exit)(exitCode);
  }
}
