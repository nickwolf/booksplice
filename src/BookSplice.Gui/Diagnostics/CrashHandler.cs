namespace BookSplice.Gui.Diagnostics;

public interface ICrashUi
{
  void ShowFatal(bool reportSaved);
  void Shutdown(int exitCode);
}

public static class CrashMessages
{
  public const string Saved = "BookSplice hit an unexpected error and must close. A diagnostic report was saved on this computer.";
  public const string NotSaved = "BookSplice hit an unexpected error and must close. A diagnostic report could not be saved.";
  public const string NoticeText = "BookSplice closed unexpectedly last time and saved a diagnostic report on this computer. Nothing is shared unless you copy the summary yourself.";
}

// One instance serves the whole process. After the first crash it ignores everything else, so a failure inside the handler cannot recurse.
public sealed class CrashHandler(Func<ICrashReportSink?> sink, ICrashUi ui, ActiveJobRegistry jobs,
  CrashLaunchMode mode, Func<string?> appVersion, TimeProvider? clock = null)
{
  private int _entered;
  private volatile bool _updateCommitted;

  // An update candidate stays silent and exits nonzero until the update is committed, so the launcher can roll back.
  public bool IsSilent => mode == CrashLaunchMode.UpdateCandidate && !_updateCommitted;

  public void MarkUpdateCommitted() => _updateCommitted = true;

  public void HandleDispatcher(Exception exception)
  {
    if (!Enter()) return;
    try
    {
      var saved = Write(exception, CrashSource.Dispatcher);
      if (!IsSilent) ui.ShowFatal(saved);
    }
    catch { }
    finally
    {
      try { ui.Shutdown(1); } catch { }
    }
  }

  public void HandleAppDomain(object? exceptionObject)
  {
    if (!Enter()) return;
    try
    {
      Write(exceptionObject as Exception ?? new InvalidOperationException("Unhandled non-exception object."), CrashSource.AppDomain);
    }
    catch { }
  }

  public bool HandleStartup(Exception exception)
  {
    if (!Enter()) return false;
    try { return Write(exception, CrashSource.Startup); }
    catch { return false; }
  }

  private bool Enter() => Interlocked.Exchange(ref _entered, 1) == 0;

  private bool Write(Exception exception, CrashSource source)
  {
    var report = CrashReport.Create(exception, source, mode, appVersion(), jobs.Snapshot(), (clock ?? TimeProvider.System).GetUtcNow());
    return sink()?.TryWrite(report) ?? false;
  }
}
