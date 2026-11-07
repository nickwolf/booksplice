using BookSplice.Core.Queue;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class QueuePersistenceTests
{
  [Fact]
  public async Task ASaveCapturedBeforeAFlushNeverOverwritesIt()
  {
    var stale = new QueueSnapshot(1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), []);
    var fresh = new QueueSnapshot(1, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), []);
    var store = new Store();
    using var captured = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    using var timerDone = new ManualResetEventSlim();
    var calls = 0;
    QueueSnapshot Capture()
    {
      if (Interlocked.Increment(ref calls) > 1) return fresh;
      captured.Set();
      release.Wait(TimeSpan.FromSeconds(10));
      return stale;
    }
    using var persistence = new QueuePersistence(store, Capture, _ => { }, TimeSpan.FromHours(1), action => { action(); timerDone.Set(); });

    // The timer save captures first and is held there while the flush captures, so the flush's snapshot is the newer one.
    persistence.Request(immediate: true);
    Assert.True(captured.Wait(TimeSpan.FromSeconds(10)));
    var flush = Task.Run(persistence.Flush);
    await Task.WhenAny(flush, Task.Delay(300));
    release.Set();
    await flush.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.True(timerDone.Wait(TimeSpan.FromSeconds(10)));

    SpinWait.SpinUntil(() => ReferenceEquals(store.Last, stale), TimeSpan.FromMilliseconds(500));
    Assert.Same(fresh, store.Last);
  }

  private sealed class Store : IQueueSnapshotStore
  {
    private QueueSnapshot? _last;
    public string Path => "memory";
    public QueueSnapshot? Last => Volatile.Read(ref _last);
    public QueueLoadResult Load() => new(QueueLoadCode.Missing, null);
    public bool Save(QueueSnapshot snapshot)
    {
      Volatile.Write(ref _last, snapshot);
      return true;
    }
  }
}
