using System.Windows;
using System.Windows.Threading;

namespace BookSplice.Gui.Updates;

public static class WpfReadiness
{
  public static Task WaitAsync(Window window, Dispatcher dispatcher, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(window);
    ArgumentNullException.ThrowIfNull(dispatcher);
    dispatcher.VerifyAccess();
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    EventHandler? rendered = null;
    EventHandler? closed = null;
    CancellationTokenRegistration registration = default;
    void Finish(Exception? error)
    {
      window.ContentRendered -= rendered;
      window.Closed -= closed;
      registration.Dispose();
      if (error is null) completion.TrySetResult();
      else completion.TrySetException(error);
    }
    rendered = (_, _) =>
    {
      if (!window.IsVisible || !window.IsLoaded)
      { Finish(new InvalidOperationException("The update window is not visible.")); return; }
      dispatcher.BeginInvoke(() =>
      {
        if (window.IsVisible && window.IsLoaded && !dispatcher.HasShutdownStarted)
          Finish(null);
        else Finish(new InvalidOperationException("The update window closed before readiness."));
      }, DispatcherPriority.Background);
    };
    closed = (_, _) => Finish(new InvalidOperationException("The update window closed before rendering."));
    window.ContentRendered += rendered;
    window.Closed += closed;
    registration = cancellationToken.Register(() => dispatcher.BeginInvoke(() =>
      Finish(new OperationCanceledException(cancellationToken))));
    return completion.Task;
  }
}
