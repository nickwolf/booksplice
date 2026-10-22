using System.Globalization;
using System.Runtime.InteropServices;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Launcher;

public static class LauncherApplication
{
  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern int MessageBoxW(nint window, string message, string title, uint flags);

  public static int Run(string[] args)
  {
    try
    {
      if (args is ["--apply", var transaction, var pid, var startTicks])
        return Apply(Guid.ParseExact(transaction, "D"),
          int.Parse(pid, NumberStyles.None, CultureInfo.InvariantCulture),
          long.Parse(startTicks, NumberStyles.None, CultureInfo.InvariantCulture));
      if (args.Length != 0) throw new ArgumentException("Unsupported launcher command.");
      var installation = OpenInstallation();
      var (layout, _, state, _, journal) = installation;
      if (new RecoveryCoordinator(journal, new ActivityLeaseStore(layout.DataRoot)).Recover() ==
          RecoveryOutcomeKind.UpdateInProgress)
        throw new InvalidOperationException(
          "A BookSplice update is in progress. Try again when it finishes.");
      Cleanup(installation);
      var decision = LaunchResolver.LaunchCurrent(layout, state);
      if (decision.Kind != LaunchDecisionKind.Ready)
        throw new InvalidDataException(decision.Reason ?? "Launch recovery is required.");
      return 0;
    }
    catch (Exception error)
    {
      ShowError(error.Message + "\n\nOpen the original BookSplice folder for manual recovery.");
      return 1;
    }
  }

  // The old window still holds directory handles while it shuts down, which would make opening the
  // installation fail with a sharing violation; the handover re-checks the exit exactly afterwards.
  private static void WaitForOldWindow(int pid, long startTicks)
  {
    try
    {
      using var old = System.Diagnostics.Process.GetProcessById(pid);
      if (Math.Abs(old.StartTime.ToUniversalTime().Ticks - startTicks) < TimeSpan.TicksPerSecond * 2)
        _ = old.WaitForExit(TimeSpan.FromSeconds(60));
    }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or
      System.ComponentModel.Win32Exception)
    { }
  }

  private static int Apply(Guid transactionId, int oldGuiPid, long oldGuiStartTicks)
  {
    WaitForOldWindow(oldGuiPid, oldGuiStartTicks);
    var installation = OpenInstallation();
    var (layout, _, state, directories, journal) = installation;
    using var self = SupervisedProcess.CaptureCurrent();
    var handover = new LaunchHandover(state, journal, new CurrentTargetStore(state, journal, layout),
      new ActivityLeaseStore(layout.DataRoot), directories, self);
    var outcome = handover.RunAsync(transactionId, oldGuiPid, oldGuiStartTicks, layout,
      CancellationToken.None).GetAwaiter().GetResult();
    if (outcome.Kind == UpdateOutcomeKind.Committed)
    {
      string? notice = null;
      try { notice = CommittedFollowUp.Run(installation); }
      catch (Exception) { }
      Cleanup(installation);
      if (notice is not null) _ = MessageBoxW(0, notice, "BookSplice update", 0x40);
      return 0;
    }
    ShowError((outcome.Reason ?? "The update could not be completed.") + (outcome.FallbackLaunched ?
      "\n\nThe previous version was started instead." :
      "\n\nOpen the original BookSplice folder for manual recovery."));
    return 1;
  }

  // Best effort: cleanup never blocks or fails a launch, and skips quietly while an update or job is active.
  private static void Cleanup(OpenedInstallation installation)
  {
    try
    {
      _ = new UpdateCleanup(installation.State, installation.Journal, installation.Directories,
        new ActivityLeaseStore(installation.Layout.DataRoot)).Run(installation.Layout);
    }
    catch (Exception) { }
  }

  private static void ShowError(string message) =>
    _ = MessageBoxW(0, message, "BookSplice update", 0x10);

  private static OpenedInstallation OpenInstallation() => InstallationOpener.Open(
    Path.GetDirectoryName(Environment.ProcessPath ??
      throw new IOException("The launcher image path is unavailable."))!);
}
