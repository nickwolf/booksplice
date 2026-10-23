using System.Collections.Concurrent;

namespace BookSplice.Gui.Diagnostics;

// Job IDs of conversions currently running, so a crash report can name them.
public sealed class ActiveJobRegistry
{
  private readonly ConcurrentDictionary<Guid, byte> _jobs = new();

  public static ActiveJobRegistry Shared { get; } = new();

  public void Add(Guid jobId) => _jobs[jobId] = 0;

  public void Remove(Guid jobId) => _jobs.TryRemove(jobId, out _);

  public IReadOnlyList<Guid> Snapshot() => _jobs.Keys.Order().ToList();

  // A running conversion, registered before its job ID is known so a fatal exit can cancel it and wait for it.
  public sealed class Tracked : IDisposable
  {
    private readonly ActiveJobRegistry _owner;
    internal Tracked(ActiveJobRegistry owner, CancellationTokenSource cancellation)
    {
      _owner = owner;
      Cancellation = cancellation;
    }
    internal CancellationTokenSource Cancellation { get; }
    internal Task? Run { get; private set; }
    public void SetTask(Task run) => Run = run;
    public void Dispose() => _owner._tracked.TryRemove(this, out _);
  }

  private readonly ConcurrentDictionary<Tracked, byte> _tracked = new();

  public Tracked Track(CancellationTokenSource cancellation)
  {
    var handle = new Tracked(this, cancellation);
    _tracked[handle] = 0;
    return handle;
  }

  public void CancelAll()
  {
    foreach (var handle in _tracked.Keys)
    {
      try { handle.Cancellation.Cancel(); } catch { }
    }
  }

  public bool WaitForAll(TimeSpan timeout)
  {
    try
    {
      var runs = _tracked.Keys.Select(handle => handle.Run).OfType<Task>().ToArray();
      return Task.WaitAll(runs, timeout);
    }
    catch { return false; }
  }
}
