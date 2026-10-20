using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Windows;

namespace BookSplice.Gui.Updates;

public sealed class WpfHandoverHost : IOldApplicationHost, IDisposable
{
  private readonly Application _application;
  private readonly Window _window;
  private readonly Dispatcher _dispatcher;
  private readonly GuiOperationGate _gate;
  private readonly Func<int> _queueCount;
  private readonly string _dataRoot;
  private readonly FileIdentity _dataRootIdentity;
  private readonly ShutdownMode _ordinaryShutdownMode;
  private GuiInstanceLease? _lease;
  private IDisposable? _freeze;
  private bool _hidden;
  private bool _exitRequested;
  private bool _disposed;

  public WpfHandoverHost(Application application, Window window, GuiOperationGate gate,
    Func<int> queueCount, string canonicalDataRoot, FileIdentity dataRootIdentity,
    GuiInstanceLease initialLease)
  {
    ArgumentNullException.ThrowIfNull(application);
    ArgumentNullException.ThrowIfNull(window);
    ArgumentNullException.ThrowIfNull(gate);
    ArgumentNullException.ThrowIfNull(queueCount);
    ArgumentNullException.ThrowIfNull(initialLease);
    ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDataRoot);
    if (!dataRootIdentity.IsValid || !Path.IsPathFullyQualified(canonicalDataRoot) ||
        Path.GetFullPath(canonicalDataRoot) != canonicalDataRoot)
      throw new ArgumentException("The GUI data root identity is invalid.", nameof(canonicalDataRoot));
    _dispatcher = application.Dispatcher;
    _dispatcher.VerifyAccess();
    if (window.Dispatcher != _dispatcher || application.MainWindow != window)
      throw new ArgumentException("The host requires the application's actual main window.", nameof(window));
    using (var root = SafeDirectoryHandle.OpenCanonical(canonicalDataRoot))
      if (root.Identity != dataRootIdentity)
        throw new InvalidDataException("The GUI data root changed before handover.");
    _application = application;
    _window = window;
    _gate = gate;
    _queueCount = queueCount;
    _dataRoot = canonicalDataRoot;
    _dataRootIdentity = dataRootIdentity;
    _ordinaryShutdownMode = application.ShutdownMode;
    _lease = initialLease;
    _window.Closing += OnWindowClosing;
  }

  public Task<bool> FreezeAsync(CancellationToken cancellationToken = default) =>
    OnDispatcher(() =>
    {
      if (_disposed || _exitRequested || _hidden || _freeze is not null) return false;
      _freeze = _gate.TryFreeze(_queueCount());
      return _freeze is not null;
    }, cancellationToken);

  public Task<bool> HideAndReleaseGuiAsync(CancellationToken cancellationToken = default) =>
    OnDispatcher(() =>
    {
      if (_disposed || _exitRequested || _freeze is null || !_gate.IsFrozen || _hidden ||
          _lease is null || _queueCount() != 0 || _gate.ActiveCount != 0)
        return false;
      var previousMode = _application.ShutdownMode;
      _application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
      try
      {
        _window.Hide();
        _hidden = true;
        var lease = _lease;
        _lease = null;
        lease.Dispose();
      }
      catch
      {
        if (!_hidden) _application.ShutdownMode = previousMode;
        throw;
      }
      return true;
    }, cancellationToken);

  public Task<bool> RestoreAndAcquireGuiAsync(CancellationToken cancellationToken = default) =>
    OnDispatcher(() =>
    {
      if (_disposed || _exitRequested || _freeze is null || !_gate.IsFrozen) return false;
      if (_hidden)
      {
        using (var root = SafeDirectoryHandle.OpenCanonical(_dataRoot))
          if (root.Identity != _dataRootIdentity) return false;
        var acquired = _lease is null ? GuiInstanceLease.TryAcquire(_dataRoot) : null;
        if (_lease is null && acquired is null) return false;
        try
        {
          using var root = SafeDirectoryHandle.OpenCanonical(_dataRoot);
          if (root.Identity != _dataRootIdentity)
            throw new InvalidDataException("The GUI data root changed during handover.");
        }
        catch
        {
          acquired?.Dispose();
          throw;
        }
        if (acquired is not null)
          _lease = acquired;
        _window.Show();
        _hidden = false;
      }
      _application.ShutdownMode = _ordinaryShutdownMode;
      _freeze.Dispose();
      _freeze = null;
      return true;
    }, cancellationToken);

  public Task ExitAsync(CancellationToken cancellationToken = default) =>
    OnDispatcher(() =>
    {
      if (_disposed) return false;
      if (_exitRequested) return true;
      _application.Shutdown();
      _exitRequested = true;
      return true;
    }, cancellationToken);

  private Task<bool> OnDispatcher(Func<bool> action, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (_dispatcher.CheckAccess()) return Task.FromResult(action());
    return _dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
  }

  private void OnWindowClosing(object? sender, CancelEventArgs e)
  {
    _dispatcher.VerifyAccess();
    if (_freeze is not null && !_exitRequested) e.Cancel = true;
  }

  public void Dispose()
  {
    _dispatcher.VerifyAccess();
    if (_disposed) return;
    _disposed = true;
    _window.Closing -= OnWindowClosing;
    _freeze?.Dispose();
    _freeze = null;
    _lease?.Dispose();
    _lease = null;
  }
}
