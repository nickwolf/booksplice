using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using BookSplice.Gui.History;

namespace BookSplice.Gui.Views;

public partial class HistoryWindow : Window
{
  private readonly ConversionHistoryViewModel _model;
  public HistoryWindow(ConversionHistoryViewModel model)
  {
    InitializeComponent();
    DataContext = _model = model;
    Loaded += async (_, _) => { Search.Focus(); await _model.LoadAsync(); };
    Closed += (_, _) => _model.Dispose();
  }

  private async void Refresh_Click(object sender, RoutedEventArgs e) => await _model.LoadAsync();

  private void Close_Click(object sender, RoutedEventArgs e) => Close();

  private void OpenFolder_Click(object sender, RoutedEventArgs e)
  {
    if (_model.Selected is not { OutputPath: { } path } || !File.Exists(path)) { _ = _model.LoadAsync(); return; }
    try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { Path.GetDirectoryName(path)! } }); }
    catch (Win32Exception exception) { MessageBox.Show(this, exception.Message, "Unable to open output folder"); }
  }

  private async void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
  {
    if (await _model.ReadDiagnosticsAsync() is not { } text)
    {
      MessageBox.Show(this, "The record could not be read. Refresh the list and try again.", "Copy diagnostics");
      return;
    }
    try
    {
      Clipboard.SetText(text);
      MessageBox.Show(this, "Diagnostics copied. Local paths were redacted.", "Copy diagnostics");
    }
    catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
    {
      MessageBox.Show(this, $"Could not copy diagnostics: {exception.Message}", "Copy diagnostics");
    }
  }

  private async void Delete_Click(object sender, RoutedEventArgs e)
  {
    if (MessageBox.Show(this, "Delete this history record? The converted audio file is not touched.", "Delete record",
        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
      await _model.DeleteSelectedAsync();
  }

  private async void Clear_Click(object sender, RoutedEventArgs e)
  {
    if (MessageBox.Show(this, "Delete every history record? Converted audio files and your source files are not touched.", "Clear history",
        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
      await _model.ClearAsync();
  }
}
