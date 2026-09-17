using System.ComponentModel;
using System.Windows;
using BookSplice.Gui.ViewModels;
using Microsoft.Win32;

namespace BookSplice.Gui.Views;

public partial class FirstLaunchWindow : Window
{
  private readonly FirstLaunchViewModel _model;
  public FirstLaunchWindow(FirstLaunchViewModel model)
  {
    InitializeComponent();
    DataContext = _model = model;
    Loaded += (_, _) => OutputFolder.Focus();
  }

  private void Browse_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFolderDialog { Title = "Choose the output folder", Multiselect = false };
    if (dialog.ShowDialog(this) == true) _model.OutputDirectory = dialog.FolderName;
  }

  private async void Save_Click(object sender, RoutedEventArgs e)
  {
    if (await _model.SaveAsync()) DialogResult = true;
  }

  protected override void OnClosing(CancelEventArgs e)
  {
    if (_model.IsBusy) e.Cancel = true;
    base.OnClosing(e);
  }
}
