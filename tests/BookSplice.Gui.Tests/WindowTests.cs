using System.Windows;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;
using BookSplice.Gui.Views;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class WindowTests
{
  [Fact]
  public void QueueItemRendersProgressAndTracksUpdates()
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        var store = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var window = new MainWindow(store, AppSettings.Defaults);
        try
        {
          var queue = Assert.IsType<System.Windows.Controls.ListBox>(window.FindName("Queue"));
          var item = new BookQueueItemViewModel("synthetic-book", AppSettings.Defaults);
          var row = Assert.IsType<System.Windows.Controls.StackPanel>(queue.ItemTemplate.LoadContent());
          row.DataContext = item;
          window.Content = row;
          window.ShowInTaskbar = false;
          window.Left = -10000;
          window.Top = -10000;
          window.Show();
          window.UpdateLayout();
          row.Measure(new Size(280, 100));
          row.Arrange(new Rect(0, 0, 280, 100));
          var progress = Assert.Single(row.Children.OfType<System.Windows.Controls.ProgressBar>());
          Assert.Equal(0, progress.Value);
          Assert.False(progress.IsIndeterminate);
          typeof(BookQueueItemViewModel).GetProperty(nameof(BookQueueItemViewModel.IsProgressIndeterminate))!.SetValue(item, true);
          Assert.True(progress.IsIndeterminate);
          typeof(BookQueueItemViewModel).GetProperty(nameof(BookQueueItemViewModel.Percent))!.SetValue(item, 37.5);
          Assert.Equal(37.5, progress.Value);
          typeof(BookQueueItemViewModel).GetProperty(nameof(BookQueueItemViewModel.IsProgressIndeterminate))!.SetValue(item, false);
          Assert.False(progress.IsIndeterminate);
        }
        finally { window.Close(); }
      }
      catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
    Assert.Null(failure);
  }
  [Fact]
  public void SetupAndMainWindowsLoadOnWindowsDispatcher()
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        var store = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var setup = new FirstLaunchWindow(new FirstLaunchViewModel(new FirstLaunchService(store), AppSettings.Defaults));
        setup.Measure(new Size(600, 450));
        setup.Arrange(new Rect(0, 0, 600, 450));
        Assert.NotNull(setup.Content);
        Assert.True(setup.MinWidth <= 600);
        setup.Close();
        var main = new MainWindow(store, AppSettings.Defaults);
        main.Measure(new Size(900, 560));
        Assert.NotNull(main.Content);
        main.Close();
      }
      catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
    Assert.Null(failure);
  }
}
