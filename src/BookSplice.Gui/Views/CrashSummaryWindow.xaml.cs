using System.Runtime.InteropServices;
using System.Windows;

namespace BookSplice.Gui.Views;

public partial class CrashSummaryWindow : Window
{
  public CrashSummaryWindow(string summary)
  {
    InitializeComponent();
    SummaryBox.Text = summary;
    Loaded += (_, _) => SummaryBox.Focus();
  }

  private void Copy_Click(object sender, RoutedEventArgs e)
  {
    try
    {
      Clipboard.SetText(SummaryBox.Text);
      Status.Text = "Summary copied.";
    }
    catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
    {
      Status.Text = "Could not copy the summary.";
    }
  }
}
