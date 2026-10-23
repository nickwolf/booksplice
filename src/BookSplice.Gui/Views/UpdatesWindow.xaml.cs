using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using BookSplice.Gui.Updates;

namespace BookSplice.Gui.Views;

public partial class UpdatesWindow : Window
{
  private readonly UpdatesViewModel _model;
  public UpdatesWindow(UpdatesViewModel model)
  {
    InitializeComponent();
    DataContext = _model = model;
    Loaded += (_, _) => Check.Focus();
  }

  private async void Check_Click(object sender, RoutedEventArgs e) => await _model.CheckAsync(manual: true);

  private async void Install_Click(object sender, RoutedEventArgs e)
  {
    await _model.InstallAsync();
    if (_model.Phase == UpdatesPhase.Available) Install.Focus();
  }

  private async void Rollback_Click(object sender, RoutedEventArgs e)
  {
    if (MessageBox.Show(this, "Restart BookSplice with the previous version? Settings and your folders are kept.",
        "Roll back BookSplice", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
      await _model.RollbackAsync();
  }

  private void Cancel_Click(object sender, RoutedEventArgs e) => _model.CancelDownload();

  private void Later_Click(object sender, RoutedEventArgs e) => Close();

  private void Close_Click(object sender, RoutedEventArgs e) => Close();

  private void OpenRelease_Click(object sender, RoutedEventArgs e)
  {
    if (_model.ReleaseUrl is not { } url) return;
    try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
    catch (Win32Exception exception) { MessageBox.Show(this, exception.Message, "Unable to open the release page"); }
  }

  protected override void OnClosing(CancelEventArgs e)
  {
    // Closing the window while a restart is in progress would hide the status the user needs.
    if (_model.Phase is UpdatesPhase.Preparing or UpdatesPhase.Restarting) e.Cancel = true;
    base.OnClosing(e);
  }
}
