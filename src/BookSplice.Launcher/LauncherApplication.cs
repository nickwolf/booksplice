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
      var (layout, state, _) = OpenInstallation();
      var journal = new UpdateJournal(state, Path.Combine(layout.Records.Path, "journal.json"),
        layout.InstallationId);
      if (new RecoveryCoordinator(journal, new ActivityLeaseStore(layout.DataRoot)).Recover() ==
          RecoveryOutcomeKind.UpdateInProgress)
        throw new InvalidOperationException(
          "A BookSplice update is in progress. Try again when it finishes.");
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

  private static int Apply(Guid transactionId, int oldGuiPid, long oldGuiStartTicks)
  {
    var (layout, state, directories) = OpenInstallation();
    var journal = new UpdateJournal(state, Path.Combine(layout.Records.Path, "journal.json"),
      layout.InstallationId);
    using var self = SupervisedProcess.CaptureCurrent();
    var handover = new LaunchHandover(state, journal, new CurrentTargetStore(state, journal, layout),
      new ActivityLeaseStore(layout.DataRoot), directories, self);
    var outcome = handover.RunAsync(transactionId, oldGuiPid, oldGuiStartTicks, layout,
      CancellationToken.None).GetAwaiter().GetResult();
    if (outcome.Kind == UpdateOutcomeKind.Committed) return 0;
    ShowError((outcome.Reason ?? "The update could not be completed.") + (outcome.FallbackLaunched ?
      "\n\nThe previous version was started instead." :
      "\n\nOpen the original BookSplice folder for manual recovery."));
    return 1;
  }

  private static void ShowError(string message) =>
    _ = MessageBoxW(0, message, "BookSplice update", 0x10);

  private static (InstallationLayout Layout, AtomicStateStore State,
    OwnedDirectoryStore Directories) OpenInstallation()
  {
    var executable = Environment.ProcessPath ??
      throw new IOException("The launcher image path is unavailable.");
    var rootPath = Path.GetDirectoryName(executable)!;
    var rootName = Path.GetFileName(rootPath);
    const string prefix = ".booksplice-managed-";
    if (!rootName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        !Guid.TryParseExact(rootName[prefix.Length..], "D", out var installationId))
      throw new InvalidDataException("The launcher is outside a recorded installation.");
    using var rootHandle = SafeDirectoryHandle.OpenPath(rootPath, false);
    using var recordsHandle = SafeDirectoryHandle.OpenGuardedChild(rootHandle, "records");
    var recordsPath = Path.Combine(rootPath, "records");
    var managed = new OwnedDirectory(rootPath, rootHandle.Identity, installationId,
      Guid.NewGuid(), "managed-root");
    var records = new OwnedDirectory(recordsPath, recordsHandle.Identity, installationId,
      Guid.NewGuid(), "records");
    var state = new AtomicStateStore(records,
    [
      InstallationRecord.Definition("installation.json", installationId),
      CurrentTarget.Definition("current.json", installationId),
      UpdateTransaction.Definition("journal.json", installationId),
      OwnershipState.Definition("ownership.json", records, managed)
    ]);
    var installationRead = state.Read<InstallationRecord>(
      Path.Combine(recordsPath, "installation.json"));
    if (installationRead.Kind != StateReadKind.Valid || installationRead.Value is not { } record ||
        record.ManagedRoot.Path != rootPath ||
        record.ManagedRoot.Identity != rootHandle.Identity ||
        record.Records.Path != recordsPath ||
        record.Records.Identity != recordsHandle.Identity ||
        record.StableLauncherIdentity is not { } launcherIdentity ||
        record.StableLauncherSha256 is not { } launcherHash)
      throw new InvalidDataException("The launcher installation record is invalid.");
    using (var image = rootHandle.OpenFile("BookSplice.Launcher.exe"))
    {
      SafeDirectoryHandle.RejectReparseFile(image);
      if (NativeFileIdentity.Read(image) != launcherIdentity ||
          !string.Equals(PackageInventory.HashHandle(image), launcherHash,
            StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The stable launcher image changed.");
    }
    rootHandle.Dispose();
    recordsHandle.Dispose();
    var directories = new OwnedDirectoryStore(
      Path.GetDirectoryName(record.OriginalApplicationPath)!,
      _ => throw new InvalidOperationException("Launch cannot create directories."));
    return (record.Restore(directories), state, directories);
  }
}
