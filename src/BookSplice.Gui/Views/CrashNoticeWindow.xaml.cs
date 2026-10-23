using System.Windows;
using BookSplice.Gui.Diagnostics;

namespace BookSplice.Gui.Views;

public partial class CrashNoticeWindow : Window
{
  public CrashNoticeWindow()
  {
    InitializeComponent();
    Message.Text = CrashMessages.NoticeText;
  }

  private void Review_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
