using System.Windows;
using System.Windows.Threading;
using BookSplice.Gui.Updates;
using BookSplice.Updates.Windows;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class HandoverHostTests
{
  [Fact]
  public void RealWindowAndNativeLeaseRemainSafeAcrossHandover()
  {
    Assert.True(Environment.UserInteractive,
      "The handover host test requires an interactive Windows desktop.");
    var parent = Path.Combine(Path.GetTempPath(), "booksplice-host-" + Guid.NewGuid().ToString("N"));
    var root = Path.Combine(parent, "BookSplice");
    Directory.CreateDirectory(root);
    try
    {
      OnSta(() =>
      {
        Application? app = null;
        Window? window = null;
        WpfHandoverHost? host = null;
        GuiInstanceLease? lease = null;
        try
        {
          using var dataRoot = SafeDirectoryHandle.OpenCanonical(root);
          lease = GuiInstanceLease.TryAcquire(root)!;
          app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
          window = new Window { Width = 16, Height = 16, ShowInTaskbar = false };
          app.MainWindow = window;
          window.Show();
          var gate = new GuiOperationGate(app.Dispatcher);
          var queueCount = 1;
          host = new WpfHandoverHost(app, window, gate, () => queueCount,
            root, dataRoot.Identity, lease);
          lease = null;

          Assert.False(host.FreezeAsync().GetAwaiter().GetResult());
          queueCount = 0;
          using (var active = gate.Enter("convert"))
            Assert.False(host.FreezeAsync().GetAwaiter().GetResult());
          Assert.True(host.FreezeAsync().GetAwaiter().GetResult());
          Assert.True(gate.IsFrozen);
          Assert.Throws<InvalidOperationException>(() => gate.Enter("late add"));
          window.Close();
          Assert.True(window.IsVisible);
          queueCount = 1;
          Assert.False(host.HideAndReleaseGuiAsync().GetAwaiter().GetResult());
          Assert.True(window.IsVisible);
          queueCount = 0;
          Assert.True(host.HideAndReleaseGuiAsync().GetAwaiter().GetResult());
          Assert.False(window.IsVisible);
          Assert.True(window.IsLoaded);
          Assert.False(app.Dispatcher.HasShutdownStarted);
          Assert.Equal(ShutdownMode.OnExplicitShutdown, app.ShutdownMode);

          using (var contender = GuiInstanceLease.TryAcquire(root)!)
          {
            Assert.False(host.RestoreAndAcquireGuiAsync().GetAwaiter().GetResult());
            Assert.False(window.IsVisible);
            Assert.True(gate.IsFrozen);
          }
          var displaced = root + "-displaced";
          dataRoot.Dispose();
          Directory.Move(root, displaced);
          Directory.CreateDirectory(root);
          Assert.False(host.RestoreAndAcquireGuiAsync().GetAwaiter().GetResult());
          Assert.False(window.IsVisible);
          Assert.True(gate.IsFrozen);
          Directory.Delete(root);
          Directory.Move(displaced, root);
          var offThreadRestore = Task.Run(() => host.RestoreAndAcquireGuiAsync());
          PumpUntil(offThreadRestore, app.Dispatcher);
          Assert.True(offThreadRestore.GetAwaiter().GetResult());
          Assert.True(window.IsVisible);
          Assert.False(gate.IsFrozen);
          Assert.Equal(ShutdownMode.OnMainWindowClose, app.ShutdownMode);
          Assert.Null(GuiInstanceLease.TryAcquire(root));

          var offThreadFreeze = Task.Run(() => host.FreezeAsync());
          PumpUntil(offThreadFreeze, app.Dispatcher);
          Assert.True(offThreadFreeze.GetAwaiter().GetResult());
          Assert.True(host.HideAndReleaseGuiAsync().GetAwaiter().GetResult());
          Assert.True(gate.IsFrozen);
          Assert.False(app.Dispatcher.HasShutdownStarted);
        }
        finally
        {
          host?.Dispose();
          lease?.Dispose();
          if (window is { IsVisible: true }) window.Close();
          app?.Shutdown();
        }
      });
    }
    finally { Directory.Delete(parent, recursive: true); }
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
