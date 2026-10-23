using System.IO;
using BookSplice.Gui.Updates;

namespace BookSplice.Gui.Tests;

public sealed class UpdateSelfCheckReadTests
{
  // The self-check blocks its UI thread on this read, so it must not resume on that thread.
  [Fact]
  public void BoundedReadDoesNotNeedTheCallersSynchronizationContext()
  {
    using var context = new SingleThreadContext();
    var completed = new ManualResetEventSlim();
    string? result = null;
    context.Post(() =>
    {
      using var reader = new StreamReader(new SlowStream("tool output"));
      result = UpdateSelfCheck.ReadBoundedAsync(reader).GetAwaiter().GetResult();
      completed.Set();
    });
    Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), "The read deadlocked its blocked caller.");
    Assert.Equal("tool output", result);
  }

  private sealed class SlowStream(string text) : MemoryStream(System.Text.Encoding.UTF8.GetBytes(text))
  {
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
      await Task.Delay(20, cancellationToken).ConfigureAwait(false);
      return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
  }

  private sealed class SingleThreadContext : SynchronizationContext, IDisposable
  {
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public SingleThreadContext()
    {
      _thread = new Thread(() =>
      {
        SetSynchronizationContext(this);
        foreach (var work in _queue.GetConsumingEnumerable()) work();
      })
      { IsBackground = true };
      _thread.Start();
    }

    public void Post(Action work) => _queue.Add(work);
    public override void Post(SendOrPostCallback d, object? state) => _queue.Add(() => d(state));
    public void Dispose() => _queue.CompleteAdding();
  }
}
