using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BookSplice.Core.Analysis;
using BookSplice.Core.Settings;
using BookSplice.Core.Validation;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class ResultWindowTests
{
  [Fact]
  public void ResultBindingsFollowTheSelectedBookAndPublication()
  {
    RunOnDispatcher(() =>
    {
      var file = Path.GetTempFileName();
      var store = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
      var window = new MainWindow(store, AppSettings.Defaults);
      try
      {
        var queue = Assert.IsType<ListBox>(window.FindName("Queue"));
        var card = Assert.IsType<Border>(window.FindName("ConversionResultCard"));
        var title = Assert.IsType<TextBlock>(window.FindName("ResultTitle"));
        var open = Assert.IsType<Button>(window.FindName("OpenResultOutput"));
        Flush(window);
        Assert.Equal(Visibility.Collapsed, card.Visibility);
        var success = new BookQueueItemViewModel("generated-book", AppSettings.Defaults);
        Set(success, nameof(BookQueueItemViewModel.Result), ConversionResultDisplay.Create(
          new(Guid.NewGuid(), ConversionTerminalStatus.Succeeded, ConversionStage.Completed, null, null, null, null, null,
            [new("warning", ServiceDiagnosticSeverity.Warning, "Unknown warning")], [], file), TimeSpan.Zero, ValidationLevel.Lightweight));
        Set(success, nameof(BookQueueItemViewModel.PublishedPath), file);
        var failed = new BookQueueItemViewModel("failed-book", AppSettings.Defaults);
        Set(failed, nameof(BookQueueItemViewModel.Result), ConversionResultDisplay.Create(
          new(Guid.NewGuid(), ConversionTerminalStatus.ValidationFailed, ConversionStage.Validation, null, null, null, null, null,
            [new("failure", ServiceDiagnosticSeverity.Error, "Required audio is missing")], [], null), TimeSpan.Zero, ValidationLevel.Full));
        queue.Items.Add(success);
        queue.Items.Add(failed);
        queue.SelectedItem = success;
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        window.ShowInTaskbar = false;
        window.Left = -10000;
        window.Top = -10000;
        window.Show();
        Flush(window);
        Assert.Equal(Visibility.Visible, card.Visibility);
        Assert.Equal("Conversion complete with warnings", title.Text);
        Assert.True(open.IsEnabled);
        queue.SelectedItem = failed;
        Flush(window);
        Assert.Equal("Conversion failed", title.Text);
        Assert.False(open.IsEnabled);
        Set(failed, nameof(BookQueueItemViewModel.Result), null);
        Flush(window);
        Assert.Equal(Visibility.Collapsed, card.Visibility);
      }
      finally { window.Close(); File.Delete(file); }
    });
  }

  private static void Flush(Window window)
  {
    window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    window.UpdateLayout();
  }
  private static void Set(BookQueueItemViewModel item, string property, object? value) => typeof(BookQueueItemViewModel).GetProperty(property)!.SetValue(item, value);
  private static void RunOnDispatcher(Action test)
  {
    Exception? failure = null;
    var thread = new Thread(() => { try { test(); } catch (Exception exception) { failure = exception; } });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
    Assert.Null(failure);
  }
}
