using System.Diagnostics;
using System.Windows.Threading;
using BookSplice.Core.Queue;

namespace BookSplice.Gui.ViewModels;

// Saves the queue after changes settle. The snapshot is built on the UI thread and written on a worker, so a slow or failing disk never blocks the UI.
internal sealed class QueuePersistence : IDisposable
{
  private readonly IQueueSnapshotStore _store;
  private readonly Func<QueueSnapshot> _capture;
  private readonly Action<string> _notify;
  private readonly Action<Action> _post;
  private readonly TimeSpan _debounce;
  private readonly Timer _timer;
  private readonly object _gate = new();
  private readonly object _writeLock = new();
  private bool _armed;
  private bool _disposed;
  private bool _requestedWhileHeld;
  private int _holds;
  private long _sequence;
  private long _written;
  private bool _errorShown;

  public QueuePersistence(IQueueSnapshotStore store, Func<QueueSnapshot> capture, Action<string> notify, TimeSpan debounce, Action<Action>? post = null)
  {
    _store = store;
    _capture = capture;
    _notify = notify;
    _debounce = debounce;
    var dispatcher = Dispatcher.CurrentDispatcher;
    _post = post ?? (action => dispatcher.BeginInvoke(action));
    _timer = new Timer(_ => _post(SaveFromTimer), null, Timeout.Infinite, Timeout.Infinite);
  }

  /// <summary>Saves are postponed, not dropped, while a restore is rebuilding the queue.</summary>
  public IDisposable Hold()
  {
    lock (_gate) _holds++;
    return new Release(this);
  }

  public void Request(bool immediate = false)
  {
    lock (_gate)
    {
      if (_disposed) return;
      if (_holds > 0) { _requestedWhileHeld = true; return; }
      if (_armed && !immediate) return;
      _armed = true;
      _timer.Change(immediate ? TimeSpan.Zero : _debounce, Timeout.InfiniteTimeSpan);
    }
  }

  /// <summary>Writes the current queue now. Used when the window closes, where a background write would be cut off.</summary>
  public void Flush()
  {
    lock (_gate)
    {
      if (_disposed || _holds > 0) return;
      _armed = false;
      _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }
    Save(background: false);
  }

  public void Dispose()
  {
    lock (_gate)
    {
      _disposed = true;
      _timer.Dispose();
    }
  }

  private void SaveFromTimer()
  {
    lock (_gate)
    {
      _armed = false;
      if (_disposed) return;
      if (_holds > 0) { _requestedWhileHeld = true; return; }
    }
    Save(background: true);
  }

  private void Save(bool background)
  {
    QueueSnapshot snapshot;
    try { snapshot = _capture(); }
    catch (Exception exception)
    {
      Report(exception);
      return;
    }
    var sequence = Interlocked.Increment(ref _sequence);
    if (background) _ = Task.Run(() => Write(sequence, snapshot));
    else Write(sequence, snapshot);
  }

  private void Write(long sequence, QueueSnapshot snapshot)
  {
    lock (_writeLock)
    {
      if (sequence <= _written) return;
      try
      {
        _store.Save(snapshot);
        _written = sequence;
        if (_errorShown)
        {
          _errorShown = false;
          _post(() => _notify(""));
        }
      }
      catch (Exception exception) { Report(exception); }
    }
  }

  private void Report(Exception exception)
  {
    Trace.TraceWarning($"The queue could not be saved: {exception.GetType().Name}: {exception.Message}");
    _errorShown = true;
    _post(() => _notify($"The queue could not be saved ({exception.Message}). BookSplice will try again after the next change."));
  }

  private void EndHold()
  {
    bool again;
    lock (_gate)
    {
      _holds--;
      again = _holds == 0 && _requestedWhileHeld;
      if (again) _requestedWhileHeld = false;
    }
    if (again) Request();
  }

  private sealed class Release(QueuePersistence owner) : IDisposable
  {
    private int _done;
    public void Dispose()
    {
      if (Interlocked.Exchange(ref _done, 1) == 0) owner.EndHold();
    }
  }
}
