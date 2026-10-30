using System.Windows;
using System.Windows.Controls;
using BookSplice.Core.Discovery;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Views;

public partial class BookGroupPreviewWindow : Window
{
  private readonly BookGroupPreviewViewModel _model;

  public BookGroupPreviewWindow(BookGroupPreviewViewModel model)
  {
    InitializeComponent();
    DataContext = _model = model;
    ContentRendered += async (_, _) => await ScanAsync();
    Closed += (_, _) => _model.CancelScan();
  }

  private async Task ScanAsync()
  {
    try { await _model.LoadAsync(); }
    catch (OperationCanceledException) { }
    ScanProgress.Visibility = Visibility.Collapsed;
  }

  public IReadOnlyList<BookGroup> AcceptedGroups => _model.AcceptedGroups;

  private BookGroupRow[] Selected => GroupList.SelectedItems.OfType<BookGroupRow>().ToArray();

  private void Combine_Click(object sender, RoutedEventArgs e) => _model.Combine(Selected);

  private void Split_Click(object sender, RoutedEventArgs e) => _model.Split(Selected);

  private void Remove_Click(object sender, RoutedEventArgs e) => _model.Remove(Selected);

  private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    => FileList.ItemsSource = Selected.SelectMany(row => row.Lines).ToArray();

  private void Add_Click(object sender, RoutedEventArgs e) => DialogResult = true;

  private void Cancel_Click(object sender, RoutedEventArgs e)
  {
    _model.CancelScan();
    DialogResult = false;
  }
}
