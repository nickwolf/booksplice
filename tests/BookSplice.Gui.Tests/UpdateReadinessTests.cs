using System.Windows;
using System.Windows.Threading;
using BookSplice.Gui.Updates;

namespace BookSplice.Gui.Tests;

public sealed class UpdateReadinessTests
{
  [Fact]
  public void NeverRenderedWindowCannotBeReady()
  {
    OnSta(() =>
    {
      var dispatcher = Dispatcher.CurrentDispatcher;
      var window = new Window();
      using var cancelled = new CancellationTokenSource();
      var wait = WpfReadiness.WaitAsync(window, dispatcher, cancelled.Token);
      Assert.False(wait.IsCompleted);
      cancelled.Cancel();
      PumpUntil(wait, dispatcher);
      Assert.True(wait.IsCanceled || wait.IsFaulted);
    });
  }

  [Fact]
  public void ClosedBeforeRenderCannotBeReady()
  {
    OnSta(() =>
    {
      var dispatcher = Dispatcher.CurrentDispatcher;
      var window = new Window();
      var wait = WpfReadiness.WaitAsync(window, dispatcher, CancellationToken.None);
      window.Close();
      Assert.True(wait.IsFaulted);
    });
  }

  [Fact]
  public void VisibleRenderedWindowNeedsLaterDispatcherTurn()
  {
    Assert.True(Environment.UserInteractive,
      "A visible WPF readiness test requires an interactive Windows desktop.");
    OnSta(() =>
    {
      var dispatcher = Dispatcher.CurrentDispatcher;
      var window = new Window
      {
        Width = 16,
        Height = 16,
        ShowInTaskbar = false,
        Content = new System.Windows.Controls.TextBlock { Text = "ready" }
      };
      var contentRendered = false;
      using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      var wait = WpfReadiness.WaitAsync(window, dispatcher, deadline.Token);
      window.ContentRendered += (_, _) =>
      {
        contentRendered = true;
        Assert.False(wait.IsCompleted);
      };
      window.Show();
      PumpUntil(wait, dispatcher);
      Assert.True(contentRendered);
      Assert.True(wait.IsCompletedSuccessfully);
      window.Close();
    });
  }

  private static void PumpUntil(Task task, Dispatcher dispatcher)
  {
    if (task.IsCompleted) return;
    var frame = new DispatcherFrame();
    task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false),
      TaskScheduler.Default);
    Dispatcher.PushFrame(frame);
  }

  private static void OnSta(Action action)
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try { action(); }
      catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
  }
}
