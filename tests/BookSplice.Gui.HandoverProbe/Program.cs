using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BookSplice.Gui.Updates;
using BookSplice.Updates.Windows;

internal static class Program
{
  [STAThread]
  private static int Main()
  {
    try
    {
      RunScenario();
      Console.WriteLine("PASS: real WPF/native handover assertions completed");
      return 0;
    }
    catch (Exception error)
    {
      Console.Error.WriteLine(error);
      return 1;
    }
  }

  private static void RunScenario()
  {
    Require(Environment.UserInteractive,
      "The handover probe requires an interactive Windows desktop.");
    var parent = Path.Combine(Path.GetTempPath(), "booksplice-host-" + Guid.NewGuid().ToString("N"));
    var root = Path.Combine(parent, "BookSplice");
    Directory.CreateDirectory(root);
    Application? app = null;
    Window? window = null;
    WpfHandoverHost? host = null;
    GuiInstanceLease? lease = null;
    try
    {
      using var dataRoot = SafeDirectoryHandle.OpenCanonical(root);
      lease = GuiInstanceLease.TryAcquire(root)
        ?? throw new InvalidOperationException("Could not acquire the initial native GUI lease.");
      app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
      window = new Window { Width = 16, Height = 16, ShowInTaskbar = false };
      app.MainWindow = window;
      window.Show();
      var gate = new GuiOperationGate(app.Dispatcher);
      var queueCount = 1;
      host = new WpfHandoverHost(app, window, gate, () => queueCount,
        root, dataRoot.Identity, lease);
      lease = null;

      Require(!host.FreezeAsync().GetAwaiter().GetResult(), "A queued item must block freezing.");
      queueCount = 0;
      using (gate.Enter("convert"))
        Require(!host.FreezeAsync().GetAwaiter().GetResult(), "An active operation must block freezing.");
      Require(host.FreezeAsync().GetAwaiter().GetResult(), "An idle host should freeze.");
      Require(gate.IsFrozen, "The operation gate should be frozen.");
      Throws<InvalidOperationException>(() => gate.Enter("late add"));
      window.Close();
      Require(window.IsVisible, "Closing the frozen window must be ignored.");
      queueCount = 1;
      Require(!host.HideAndReleaseGuiAsync().GetAwaiter().GetResult(), "Queued work must block release.");
      Require(window.IsVisible, "A blocked release must leave the window visible.");
      queueCount = 0;
      Require(host.HideAndReleaseGuiAsync().GetAwaiter().GetResult(), "An idle frozen host should hide and release.");
      Require(!window.IsVisible && window.IsLoaded, "Release should hide while preserving the loaded window.");
      Require(!app.Dispatcher.HasShutdownStarted, "Hiding must not shut down the dispatcher.");
      Require(app.ShutdownMode == ShutdownMode.OnExplicitShutdown,
        "A released host should use explicit shutdown mode.");

      using (var contender = GuiInstanceLease.TryAcquire(root)
        ?? throw new InvalidOperationException("A contender could not acquire the released lease."))
      {
        Require(!host.RestoreAndAcquireGuiAsync().GetAwaiter().GetResult(),
          "Restore must fail while another process owns the GUI lease.");
        Require(!window.IsVisible && gate.IsFrozen, "Failed lease acquisition must preserve hidden/frozen state.");
      }
      var displaced = root + "-displaced";
      dataRoot.Dispose();
      Directory.Move(root, displaced);
      Directory.CreateDirectory(root);
      Require(!host.RestoreAndAcquireGuiAsync().GetAwaiter().GetResult(),
        "Restore must fail when the data root identity changes.");
      Require(!window.IsVisible && gate.IsFrozen, "Root mismatch must preserve hidden/frozen state.");
      Directory.Delete(root);
      Directory.Move(displaced, root);

      var offThreadRestore = Task.Run(() => host.RestoreAndAcquireGuiAsync());
      PumpUntil(offThreadRestore, app.Dispatcher);
      Require(offThreadRestore.GetAwaiter().GetResult(), "Off-thread restore should marshal successfully.");
      Require(window.IsVisible && !gate.IsFrozen, "Restore should reveal the window and thaw the gate.");
      Require(app.ShutdownMode == ShutdownMode.OnMainWindowClose,
        "Restoring should reinstate the original shutdown mode.");
      using (var unexpectedLease = GuiInstanceLease.TryAcquire(root))
        Require(unexpectedLease is null, "Restored host must own the native GUI lease.");

      var offThreadFreeze = Task.Run(() => host.FreezeAsync());
      PumpUntil(offThreadFreeze, app.Dispatcher);
      Require(offThreadFreeze.GetAwaiter().GetResult(), "Off-thread freeze should marshal successfully.");
      Require(host.HideAndReleaseGuiAsync().GetAwaiter().GetResult(), "The frozen host should hide and release again.");
      Require(gate.IsFrozen && !app.Dispatcher.HasShutdownStarted,
        "A second release must remain frozen without shutting down the dispatcher.");

      var applicationExitObserved = false;
      app.Exit += (_, _) => applicationExitObserved = true;
      var exitRequest = host.ExitAsync();
      Require(exitRequest.IsCompletedSuccessfully, "The exit request should complete immediately on the dispatcher.");
      exitRequest.GetAwaiter().GetResult();
      var exitFrame = new DispatcherFrame();
      app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
        new Action(() => exitFrame.Continue = false));
      Dispatcher.PushFrame(exitFrame);
      Require(applicationExitObserved, "Application.Exit must be observed after ExitAsync.");
      Require(!Process.GetCurrentProcess().HasExited, "ExitAsync must shut down WPF without terminating the host process.");
    }
    finally
    {
      host?.Dispose();
      lease?.Dispose();
      if (window is { IsVisible: true }) window.Close();
      app?.Shutdown();
      if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
    }
  }

  private static void PumpUntil(Task task, Dispatcher dispatcher)
  {
    if (task.IsCompleted) return;
    var frame = new DispatcherFrame();
    task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false),
      TaskScheduler.Default);
    Dispatcher.PushFrame(frame);
  }

  private static void Require(bool condition, string message)
  {
    if (!condition) throw new InvalidOperationException(message);
  }

  private static void Throws<TException>(Action action) where TException : Exception
  {
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
  }
}
