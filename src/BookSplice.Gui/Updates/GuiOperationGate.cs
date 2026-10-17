using System.Windows.Threading;

namespace BookSplice.Gui.Updates;

public sealed class GuiOperationGate(Dispatcher dispatcher)
{
  private readonly Dispatcher _dispatcher = dispatcher;
  private int _activeCount;
  private bool _frozen;

  public bool IsFrozen { get { _dispatcher.VerifyAccess(); return _frozen; } }
  public int ActiveCount { get { _dispatcher.VerifyAccess(); return _activeCount; } }

  public IDisposable Enter(string operation)
  {
    _dispatcher.VerifyAccess();
    ArgumentException.ThrowIfNullOrWhiteSpace(operation);
    if (_frozen) throw new InvalidOperationException("The GUI is preparing an update.");
    _activeCount++;
    return new Token(this, freeze: false);
  }

  public IDisposable? TryFreeze(int queueCount)
  {
    _dispatcher.VerifyAccess();
    ArgumentOutOfRangeException.ThrowIfNegative(queueCount);
    if (_frozen || queueCount > 0 || _activeCount > 0) return null;
    _frozen = true;
    return new Token(this, freeze: true);
  }

  private sealed class Token(GuiOperationGate owner, bool freeze) : IDisposable
  {
    private bool _disposed;
    public void Dispose()
    {
      owner._dispatcher.VerifyAccess();
      if (_disposed) return;
      _disposed = true;
      if (freeze) owner._frozen = false;
      else owner._activeCount--;
    }
  }
}
