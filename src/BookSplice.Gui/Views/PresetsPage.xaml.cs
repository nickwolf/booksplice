using System.Windows;
using System.Windows.Controls;
using BookSplice.Gui.ViewModels;
using Microsoft.Win32;

namespace BookSplice.Gui.Views;

public partial class PresetsPage : UserControl
{
  private readonly PresetsViewModel _model;
  private readonly Func<BookQueueItemViewModel?> _selectedBook;

  public PresetsPage(PresetsViewModel model, Func<BookQueueItemViewModel?> selectedBook)
  {
    InitializeComponent();
    _model = model;
    _selectedBook = selectedBook;
    DataContext = model;
    model.Reload();
  }

  private Window? Owner => Window.GetWindow(this);

  private string? SelectedName => (List.SelectedItem as PresetEntryViewModel)?.Name;

  private void List_Changed(object sender, SelectionChangedEventArgs e)
    => Details.Text = List.SelectedItems.Count == 1 ? ((PresetEntryViewModel)List.SelectedItem).Details : "";

  private void Rename_Click(object sender, RoutedEventArgs e)
  {
    if (SelectedName is not { } name) return;
    var prompt = new PresetNameWindow("Rename preset", $"New name for '{name}'.", name, offerOptional: false) { Owner = Owner };
    if (prompt.ShowDialog() == true) _model.Rename(name, prompt.EnteredName);
  }

  private void Update_Click(object sender, RoutedEventArgs e)
  {
    if (List.SelectedItem is not PresetEntryViewModel { Name: var name, Preset: var current }) return;
    if (_selectedBook() is not { } book) { MessageBox.Show(Owner, "Select a book in the main window first.", "Update preset"); return; }
    var prompt = new PresetNameWindow("Update preset", $"Replace the values of '{name}' with the settings of the selected book.", name, offerOptional: true, templateDefault: current.OutputTemplate is not null, folderDefault: current.OutputDirectory is not null) { Owner = Owner };
    prompt.NameBox.IsEnabled = false;
    if (prompt.ShowDialog() == true) _model.UpdateFrom(name, book.Settings, prompt.IncludeOutputFolder, prompt.IncludeOutputTemplate);
  }

  private void Delete_Click(object sender, RoutedEventArgs e)
  {
    if (SelectedName is not { } name) return;
    if (MessageBox.Show(Owner, $"Delete the preset '{name}'? Books that already used it are not changed.", "Delete preset", MessageBoxButton.YesNo) == MessageBoxResult.Yes) _model.Delete(name);
  }

  private void Export_Click(object sender, RoutedEventArgs e)
  {
    var names = List.SelectedItems.OfType<PresetEntryViewModel>().Select(entry => entry.Name).ToArray();
    if (_model.Presets.Count == 0) { MessageBox.Show(Owner, "There are no presets to export.", "Export presets"); return; }
    new PresetExportWindow(_model, names) { Owner = Owner }.ShowDialog();
  }

  private void Import_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFileDialog { Title = "Import presets", Filter = "BookSplice presets|*.json|All files|*.*" };
    if (dialog.ShowDialog(Owner) == true) _model.ImportFile(dialog.FileName);
  }
}
