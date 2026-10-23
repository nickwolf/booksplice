using System.Windows.Threading;
using BookSplice.Gui.Updates;

namespace BookSplice.Gui.Tests;

public sealed class UpdateOperationGateTests
{
  [Fact]
  public void QueueAndActiveWorkPreventFreeze()
  {
    OnDispatcher(() =>
    {
      var gate = new GuiOperationGate(Dispatcher.CurrentDispatcher);
      Assert.Null(gate.TryFreeze(1));
      using var operation = gate.Enter("cover");
      Assert.Equal(1, gate.ActiveCount);
      Assert.Null(gate.TryFreeze(0));
      operation.Dispose();
      Assert.Equal(0, gate.ActiveCount);
      using var freeze = gate.TryFreeze(0);
      Assert.NotNull(freeze);
      Assert.True(gate.IsFrozen);
      var callbackEntered = false;
      Exception? callbackFailure = null;
      var frame = new DispatcherFrame();
      Dispatcher.CurrentDispatcher.BeginInvoke(() =>
      {
        callbackFailure = Record.Exception(() => gate.Enter("delayed preview"));
        callbackEntered = true;
        frame.Continue = false;
      });
      Dispatcher.PushFrame(frame);
      Assert.True(callbackEntered);
      Assert.IsType<InvalidOperationException>(callbackFailure);
      freeze.Dispose();
      Assert.False(gate.IsFrozen);
      using var resumed = gate.Enter("add");
      Assert.Equal(1, gate.ActiveCount);
    });
  }

  [Fact]
  public void OffDispatcherDisposeCannotConsumeTokens()
  {
    OnDispatcher(() =>
    {
      var gate = new GuiOperationGate(Dispatcher.CurrentDispatcher);
      var operation = gate.Enter("analysis");
      var exception = Task.Run(() => Record.Exception(operation.Dispose)).GetAwaiter().GetResult();
      Assert.IsType<InvalidOperationException>(exception);
      Assert.Equal(1, gate.ActiveCount);
      operation.Dispose();
      var freeze = gate.TryFreeze(0)!;
      exception = Task.Run(() => Record.Exception(freeze.Dispose)).GetAwaiter().GetResult();
      Assert.IsType<InvalidOperationException>(exception);
      Assert.True(gate.IsFrozen);
      freeze.Dispose();
      Assert.False(gate.IsFrozen);
      Assert.IsType<InvalidOperationException>(Task.Run(() => Record.Exception(() => _ = gate.ActiveCount)).GetAwaiter().GetResult());
    });
  }

  private static void OnDispatcher(Action action)
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
