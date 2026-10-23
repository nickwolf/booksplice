using System.Windows.Threading;

namespace BookSplice.Gui.Tests;

internal static class DispatcherTestHost
{
  // Runs the test on an STA thread whose Dispatcher drives its awaits, like the real UI thread.
  public static void Run(Func<Dispatcher, Task> test)
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var task = test(dispatcher);
        task.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background), TaskScheduler.Default);
        Dispatcher.Run();
        task.GetAwaiter().GetResult();
      }
      catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The dispatcher test timed out.");
    if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
  }
}
