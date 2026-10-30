using System.Windows;
using BookSplice.Gui.ViewModels;
using Microsoft.Win32;

namespace BookSplice.Gui.Views;

public partial class PresetExportWindow : Window
{
  private readonly PresetsViewModel _model;
  private readonly IReadOnlyCollection<string> _names;

  public PresetExportWindow(PresetsViewModel model, IReadOnlyCollection<string> names)
  {
    InitializeComponent();
    _model = model;
    _names = names;
    Refresh();
  }

  private void Refresh() => Preview.Text = _model.BuildExport(_names, IncludeFolder.IsChecked == true);

  private void Include_Changed(object sender, RoutedEventArgs e)
  {
    if (Preview is not null) Refresh();
  }

  private void Save_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new SaveFileDialog { Title = "Export presets", Filter = "BookSplice presets|*.json", FileName = "booksplice-presets.json", DefaultExt = ".json" };
    if (dialog.ShowDialog(this) != true) return;
    if (_model.ExportToFile(dialog.FileName, _names, IncludeFolder.IsChecked == true)) DialogResult = true;
    else MessageBox.Show(this, _model.Message, "Export presets");
  }
}
