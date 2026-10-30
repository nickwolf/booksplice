using System.Windows;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Views;

public partial class ImportChoiceWindow : Window
{
  public ImportChoiceWindow(IReadOnlyList<string> files)
  {
    InitializeComponent();
    Heading.Text = $"Import these {files.Count} files as one audiobook, or as separate books?";
    Files.ItemsSource = files;
  }

  public FileImportChoice Choice { get; private set; } = FileImportChoice.Cancel;

  private void OneBook_Click(object sender, RoutedEventArgs e) => Choose(FileImportChoice.OneBook);

  private void Separate_Click(object sender, RoutedEventArgs e) => Choose(FileImportChoice.SeparateBooks);

  private void Choose(FileImportChoice choice)
  {
    Choice = choice;
    DialogResult = true;
  }
}
