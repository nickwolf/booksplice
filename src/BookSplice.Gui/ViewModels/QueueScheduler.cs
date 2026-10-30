namespace BookSplice.Gui.ViewModels;

// Hands out conversion slots in queue order. Pausing only stops new starts; running books keep their slots.
public sealed class QueueScheduler(int capacity)
{
  private readonly object _lock = new();
  private readonly List<Waiter> _waiting = [];
  private int _capacity = Math.Max(1, capacity);
  private int _running;
  private bool _paused;

  public int Capacity
  {
    get { lock (_lock) return _capacity; }
    set { lock (_lock) { _capacity = Math.Max(1, value); Grant(); } }
  }

  public bool IsPaused { get { lock (_lock) return _paused; } }
  public int Running { get { lock (_lock) return _running; } }

  public void Pause() { lock (_lock) _paused = true; }
  public void Resume() { lock (_lock) { _paused = false; Grant(); } }

  // Rank is the book's queue position; a lower rank starts first.
  public Task WaitAsync(object key, int rank, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var waiter = new Waiter(key, rank);
    lock (_lock)
    {
      if (_waiting.Any(other => ReferenceEquals(other.Key, key))) throw new InvalidOperationException("This book is already waiting for a slot.");
      _waiting.Add(waiter);
      Grant();
    }
    var registration = cancellationToken.Register(() =>
    {
      lock (_lock) if (!_waiting.Remove(waiter)) return;
      waiter.Turn.TrySetCanceled(cancellationToken);
    });
    // Unregister never blocks, so it is safe under the lock while a cancellation callback waits for it.
    lock (_lock) { if (_waiting.Contains(waiter)) waiter.Registration = registration; else registration.Unregister(); }
    return waiter.Turn.Task;
  }

  public void Release()
  {
    lock (_lock)
    {
      if (_running == 0) throw new InvalidOperationException("No conversion slot is held.");
      _running--;
      Grant();
    }
  }

  public void Reorder(IEnumerable<object> order)
  {
    lock (_lock)
    {
      var rank = 0;
      foreach (var key in order) foreach (var waiter in _waiting.Where(waiter => ReferenceEquals(waiter.Key, key))) waiter.Rank = rank++;
    }
  }

  private void Grant()
  {
    while (!_paused && _running < _capacity && _waiting.Count > 0)
    {
      var next = _waiting.MinBy(waiter => waiter.Rank)!;
      _waiting.Remove(next);
      _running++;
      next.Registration.Unregister();
      next.Turn.TrySetResult();
    }
  }

  private sealed class Waiter(object key, int rank)
  {
    public object Key { get; } = key;
    public int Rank { get; set; } = rank;
    public TaskCompletionSource Turn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationTokenRegistration Registration { get; set; }
  }
}
