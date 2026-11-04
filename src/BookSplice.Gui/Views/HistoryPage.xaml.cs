using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BookSplice.Gui.History;

namespace BookSplice.Gui.Views;

public partial class HistoryPage : UserControl
{
  private readonly ConversionHistoryViewModel _model;
  private bool _loaded;

  public HistoryPage(ConversionHistoryViewModel model)
  {
    InitializeComponent();
    DataContext = _model = model;
    Loaded += async (_, _) =>
    {
      if (_loaded) return;
      _loaded = true;
      await _model.LoadAsync();
    };
  }

  private Window? Owner => Window.GetWindow(this);

  private async void Refresh_Click(object sender, RoutedEventArgs e) => await _model.LoadAsync();

  // A row button acts on its own record, so it selects that record first and then uses the selection-based model.
  private bool SelectRecord(object sender)
  {
    if ((sender as FrameworkElement)?.DataContext is not ConversionHistoryEntry entry) return false;
    _model.Selected = entry;
    return true;
  }

  private void OpenFolder_Click(object sender, RoutedEventArgs e)
  {
    if (!SelectRecord(sender)) return;
    if (_model.Selected is not { OutputPath: { } path } || !File.Exists(path)) { _ = _model.LoadAsync(); return; }
    try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { Path.GetDirectoryName(path)! } }); }
    catch (Win32Exception exception) { MessageBox.Show(Owner, exception.Message, "Unable to open output folder"); }
  }

  private async void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
  {
    if (!SelectRecord(sender)) return;
    if (await _model.ReadDiagnosticsAsync() is not { } text)
    {
      MessageBox.Show(Owner, "The record could not be read. Refresh the list and try again.", "Copy diagnostics");
      return;
    }
    try
    {
      Clipboard.SetText(text);
      MessageBox.Show(Owner, "Diagnostics copied. Local paths were redacted.", "Copy diagnostics");
    }
    catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
    {
      MessageBox.Show(Owner, $"Could not copy diagnostics: {exception.Message}", "Copy diagnostics");
    }
  }

  private async void Delete_Click(object sender, RoutedEventArgs e)
  {
    if (!SelectRecord(sender)) return;
    if (MessageBox.Show(Owner, "Delete this history record? The converted audio file is not touched.", "Delete record",
        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
      await _model.DeleteSelectedAsync();
  }

  private async void Clear_Click(object sender, RoutedEventArgs e)
  {
    if (MessageBox.Show(Owner, "Delete every history record? Converted audio files and your source files are not touched.", "Clear history",
        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
      await _model.ClearAsync();
  }
}
