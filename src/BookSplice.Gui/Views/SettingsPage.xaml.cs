using System.Windows;
using System.Windows.Controls;
using BookSplice.Gui.ViewModels;
using Microsoft.Win32;

namespace BookSplice.Gui.Views;

public partial class SettingsPage : UserControl
{
  public SettingsPage() => InitializeComponent();

  public void FocusFirst() => OutputFolder.Focus();

  private void Browse_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFolderDialog { Title = "Choose the output folder", Multiselect = false };
    if (dialog.ShowDialog(Window.GetWindow(this)) == true && DataContext is FirstLaunchViewModel model) model.OutputDirectory = dialog.FolderName;
  }
}
