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
}
