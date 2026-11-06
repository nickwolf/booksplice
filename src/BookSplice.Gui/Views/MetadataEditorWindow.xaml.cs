using System.Windows;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Views;

public partial class MetadataEditorWindow : Window
{
  public MetadataEditorWindow(string bookName, IEnumerable<MetadataFieldViewModel> fields)
  {
    InitializeComponent();
    Heading.Text = bookName;
    Rows = fields.Select(field => new MetadataEditorRow(field)).ToArray();
    Fields.ItemsSource = Rows;
  }

  public IReadOnlyList<MetadataEditorRow> Rows { get; }

  // Only fields whose text changed are written, so a field left alone does not count as edited.
  public void Apply()
  {
    foreach (var row in Rows.Where(row => row.IsChanged)) row.Field.Value = row.Text;
  }

  private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
