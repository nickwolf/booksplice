using System.Windows;

namespace BookSplice.Gui.Diagnostics;

public sealed class WpfCrashUi : ICrashUi
{
  public void ShowFatal(bool reportSaved) =>
    MessageBox.Show(reportSaved ? CrashMessages.Saved : CrashMessages.NotSaved, "BookSplice", MessageBoxButton.OK, MessageBoxImage.Error);

  public void Shutdown(int exitCode) => Application.Current?.Shutdown(exitCode);
}
