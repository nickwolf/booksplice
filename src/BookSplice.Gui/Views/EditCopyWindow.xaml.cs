using System.IO;
using System.Windows;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Views;

public partial class EditCopyWindow : Window
{
  public EditCopyWindow(string bookName, EditCopyViewModel model)
  {
    InitializeComponent();
    Model = model;
    DataContext = model;
    Heading.Text = string.IsNullOrWhiteSpace(bookName) ? Path.GetFileName(model.SourcePath) : bookName;
  }

  public EditCopyViewModel Model { get; }

  private void Create_Click(object sender, RoutedEventArgs e)
  {
    if (Model.CanCreate) DialogResult = true;
  }
}
