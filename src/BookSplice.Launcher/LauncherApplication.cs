using System.Globalization;
using System.Runtime.InteropServices;
using BookSplice.Updates;
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
    // Setup runs these silently, so they sit outside the dialog-showing catch below.
    if (args is ["--register"] or ["--register", "--replace-shortcut"])
      return InstallerCommands.Register(InstallDirectory(), InstallerCommands.DataRoot(),
        args.Length == 2, Console.Error);
    if (args is ["--installed"])
      return InstallerCommands.IsInstalled(InstallDirectory(), InstallerCommands.DataRoot());
    if (args is ["--unregister"])
      // Test builds can point the toast key elsewhere so installer tests never delete the user's own key.
      return InstallerCommands.Unregister(InstallDirectory(), InstallerCommands.DataRoot(), Console.Error,
        toastKeyPath: UpdateBuild.TestSetting("BOOKSPLICE_TEST_TOAST_KEY") is { Length: > 0 } toastKey ?
          toastKey : @"Software\Classes\AppUserModelId\" + ToastIdentity.Current.AppUserModelId,
        toasts: ToastRegistration.ForCurrentUser(ToastIdentity.Current));
    // Windows starts this for a click on a toast from the notification list; COM appends -Embedding.
    if (args is [ToastRegistration.ActivatedArgument, ..] && args[1..].All(IsComEmbedding))
    {
      if (!WaitForToastActivation() || GuiIsRunning()) return 0;
      args = [];
    }
    try
    {
      if (args is ["--apply", var transaction, var pid, var startTicks])
        return Apply(Guid.ParseExact(transaction, "D"),
          int.Parse(pid, NumberStyles.None, CultureInfo.InvariantCulture),
          long.Parse(startTicks, NumberStyles.None, CultureInfo.InvariantCulture));
      if (args.Length != 0) throw new ArgumentException("Unsupported launcher command.");
      var installation = OpenInstallation();
      var (layout, _, state, _, journal) = installation;
      var recovery = new RecoveryCoordinator(journal, new ActivityLeaseStore(layout.DataRoot),
        new CurrentTargetStore(state, journal, layout)).Recover();
      if (recovery == RecoveryOutcomeKind.UpdateInProgress)
        throw new InvalidOperationException(
          "A BookSplice update is in progress. Try again when it finishes.");
      if (recovery == RecoveryOutcomeKind.CandidateStillRunning)
        throw new InvalidOperationException(
          "The new BookSplice version from an interrupted update is still running. Close it, then start BookSplice again.");
      Cleanup(installation);
      // A normal start checks identity and length only; the update handover keeps the full hash.
      var decision = LaunchResolver.LaunchCurrent(layout, state, verifyContents: false);
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

  private static bool IsComEmbedding(string argument) =>
    argument is "-Embedding" or "/Embedding" or "-embedding" or "/embedding";

  // The click is delivered through the class object; nothing in it is used beyond the fact that it came.
  // Registration and the wait run on an MTA thread, because this STA main thread pumps no messages.
  private static bool WaitForToastActivation()
  {
    using var clicked = new ManualResetEventSlim();
    var thread = new Thread(() =>
    {
      try
      {
        var identity = ToastIdentity.Current;
        using var server = ToastActivationServer.Start(identity.ActivatorClsid, identity.AppUserModelId,
          _ => clicked.Set());
        _ = clicked.Wait(TimeSpan.FromSeconds(10));
      }
      // A click that can't be received just doesn't open BookSplice.
      catch (Exception) { }
    });
    thread.SetApartmentState(ApartmentState.MTA);
    thread.Start();
    thread.Join();
    return clicked.IsSet;
  }

  // A running window registers its own class object, so a click only lands here in a race with it starting.
  // Windows starts this without BookSplice's environment, so the data root comes from the installation record.
  private static bool GuiIsRunning()
  {
    try
    {
      using var lease = GuiInstanceLease.TryAcquire(OpenInstallation().Layout.DataRoot);
      return lease is null;
    }
    catch (Exception)
    {
      // The normal launch below reports whatever is wrong with the installation.
      return false;
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

  private static string InstallDirectory() => Path.GetDirectoryName(Environment.ProcessPath ??
    throw new IOException("The launcher image path is unavailable."))!;

  private static OpenedInstallation OpenInstallation() => InstallationOpener.Open(
    InstallDirectory(), hashLauncher: false);
}
