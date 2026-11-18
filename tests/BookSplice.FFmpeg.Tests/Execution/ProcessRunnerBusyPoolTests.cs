using System.Diagnostics;
using System.Runtime.ExceptionServices;
using BookSplice.FFmpeg.Execution;

namespace BookSplice.FFmpeg.Tests.Execution;

// Caps and fills the thread pool for the whole test process, so nothing else may run alongside it.
[CollectionDefinition(nameof(ProcessRunnerBusyPoolTests), DisableParallelization = true)]
public sealed class BusyPoolGroup;

[Collection(nameof(ProcessRunnerBusyPoolTests))]
public sealed class ProcessRunnerBusyPoolTests
{
  [Fact]
  public void CancellingStopsTheProcessWhileEveryThreadPoolThreadIsBusy()
  {
    Exception? failure = null;
    // The test body needs no pool thread of its own.
    var thread = new Thread(() =>
    {
      try { CancelWithABusyPool(); }
      catch (Exception exception) { failure = exception; }
    });
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "The test did not finish.");
    if (failure is not null) ExceptionDispatchInfo.Throw(failure);
  }

  private static void CancelWithABusyPool()
  {
    ThreadPool.GetMinThreads(out var minWorkers, out _);
    ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
    // Not disposed: queued work items that have not started yet still wait on it after the test returns.
    var release = new ManualResetEventSlim();
    using var cancellation = new CancellationTokenSource();
    var processId = 0;
    Task<ProcessResult> run;
    Process? child = null;
    Assert.True(ThreadPool.SetMaxThreads(minWorkers, maxIo));
    try
    {
      for (var item = 0; item < maxWorkers + 64 && item < 4096; item++) ThreadPool.UnsafeQueueUserWorkItem(_ => release.Wait(), null);
      var busy = Stopwatch.StartNew();
      while (ThreadPool.PendingWorkItemCount == 0 && busy.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(50);
      Assert.True(ThreadPool.PendingWorkItemCount > 0, "The thread pool never filled up.");

      ProcessRunner.ProcessStarted.Value = id => processId = id;
      run = new ProcessRunner().RunAsync(new ProcessSpec(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ["-n", "60", "127.0.0.1"]), null, cancellation.Token);
      ProcessRunner.ProcessStarted.Value = null;
      Assert.NotEqual(0, processId);
      child = Process.GetProcessById(processId);

      cancellation.Cancel();
      Assert.True(child.WaitForExit(TimeSpan.FromSeconds(10)), "The process kept running while the thread pool was busy.");
    }
    finally
    {
      release.Set();
      ThreadPool.SetMaxThreads(maxWorkers, maxIo);
      if (child is { HasExited: false }) child.Kill();
      child?.Dispose();
    }
    var outcome = Assert.ThrowsAny<AggregateException>(() => run.Wait(TimeSpan.FromSeconds(30)));
    Assert.IsAssignableFrom<OperationCanceledException>(outcome.InnerException);
  }
}
