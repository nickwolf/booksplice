using System.Runtime.InteropServices;
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
      if (args.Length != 0)
      {
        if (args.Length == 1 && args[0] is "--coordinate" or "--rollback" or "--recover")
          throw new InvalidOperationException(
            "Supervised update and recovery require the transaction coordinator.");
        throw new ArgumentException("Unsupported launcher command.");
      }
      var executable = Environment.ProcessPath ??
        throw new IOException("The launcher image path is unavailable.");
      var rootPath = Path.GetDirectoryName(executable)!;
      var rootName = Path.GetFileName(rootPath);
      const string prefix = ".booksplice-managed-";
      if (!rootName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
          !Guid.TryParseExact(rootName[prefix.Length..], "D", out var installationId))
        throw new InvalidDataException("The launcher is outside a recorded installation.");
      using var rootHandle = SafeDirectoryHandle.OpenPath(rootPath, false);
      var managed = new OwnedDirectory(rootPath, rootHandle.Identity, installationId,
        Guid.NewGuid(), "managed-root");
      var state = new AtomicStateStore(managed,
      [
        InstallationRecord.Definition("installation.json", installationId),
        CurrentTarget.Definition("current.json", installationId),
        UpdateTransaction.Definition("journal.json", installationId),
        OwnershipState.Definition("ownership.json", managed)
      ]);
      var installationRead = state.Read<InstallationRecord>(
        Path.Combine(rootPath, "installation.json"));
      if (installationRead.Kind != StateReadKind.Valid || installationRead.Value is not { } record ||
          record.ManagedRoot.Path != rootPath ||
          record.ManagedRoot.Identity != rootHandle.Identity ||
          record.StableLauncherIdentity is not { } launcherIdentity ||
          record.StableLauncherSha256 is not { } launcherHash)
        throw new InvalidDataException("The launcher installation record is invalid.");
      using var image = rootHandle.OpenFile("BookSplice.Launcher.exe");
      SafeDirectoryHandle.RejectReparseFile(image);
      if (NativeFileIdentity.Read(image) != launcherIdentity ||
          !string.Equals(PackageInventory.HashHandle(image), launcherHash,
            StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The stable launcher image changed.");
      image.Dispose();
      rootHandle.Dispose();
      var directories = new OwnedDirectoryStore(
        Path.GetDirectoryName(record.OriginalApplicationPath)!,
        _ => throw new InvalidOperationException("Launch cannot create directories."));
      var layout = record.Restore(directories);
      var snapshot = new JournalSnapshot(installationRead,
        state.Read<CurrentTarget>(Path.Combine(rootPath, "current.json")),
        state.Read<UpdateTransaction>(Path.Combine(rootPath, "journal.json")),
        state.Read<OwnershipState>(Path.Combine(rootPath, "ownership.json")));
      var decision = LaunchResolver.Resolve(layout, snapshot);
      if (decision.Kind != LaunchDecisionKind.Ready)
        throw new InvalidDataException(decision.Reason ?? "Launch recovery is required.");
      var freshSnapshot = new JournalSnapshot(
        state.Read<InstallationRecord>(Path.Combine(rootPath, "installation.json")),
        state.Read<CurrentTarget>(Path.Combine(rootPath, "current.json")),
        state.Read<UpdateTransaction>(Path.Combine(rootPath, "journal.json")),
        state.Read<OwnershipState>(Path.Combine(rootPath, "ownership.json")));
      var beforeStart = LaunchResolver.Resolve(layout, freshSnapshot);
      if (beforeStart.Kind != LaunchDecisionKind.Ready ||
          !LaunchResolver.SameTarget(beforeStart.Target, decision.Target))
        throw new InvalidDataException("The committed launch mapping changed.");
      using var child = LaunchResolver.StartVerified(layout, freshSnapshot, beforeStart, [],
        LaunchResolver.ChildEnvironment(layout.DataRoot, layout.DataIdentity));
      return 0;
    }
    catch (Exception error)
    {
      _ = MessageBoxW(0, error.Message + "\n\nOpen the original BookSplice folder for manual recovery.",
        "BookSplice recovery", 0x10);
      return 1;
    }
  }
}
