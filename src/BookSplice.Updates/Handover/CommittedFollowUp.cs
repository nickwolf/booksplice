using System.Runtime.Versioning;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Handover;

// Runs after a Committed handover: binds the committed executable and ensures the Start Menu shortcut.
[SupportedOSPlatform("windows")]
public static class CommittedFollowUp
{
  // Returns a message when an unowned shortcut blocks the managed one, otherwise null.
  public static string? Run(OpenedInstallation installation, string? programsDirectory = null)
  {
    var layout = installation.Layout;
    var current = installation.State.Read<CurrentTarget>(
      Path.Combine(installation.RecordsPath, "current.json")).Value?.Current;
    if (current is { Kind: TargetKind.Managed, OwnedDirectory: { } owned })
      InstallationRegistry.Open(layout.DataRoot, create: true)!.Bind(new InstallationBinding(
        current.ExecutablePath, current.DirectoryIdentity, current.ExecutableIdentity,
        layout.InstallationId, TargetKind.Managed, layout.ManagedRoot.Path,
        layout.ManagedRoot.Identity), installation.Record);
    var shortcut = new ManagedShortcutStore(installation.Record, installation.State,
      Path.Combine(installation.RecordsPath, "shortcut.json"), programsDirectory)
      .EnsureRecordedShortcut(layout);
    return shortcut == ShortcutResult.Collision ?
      "A different BookSplice shortcut already exists and was left alone. Start BookSplice with " +
      "updates from: " + layout.StableLauncherPath : null;
  }
}
