using System.Runtime.Versioning;
using BookSplice.Updates.Storage;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Updates.Windows;

[SupportedOSPlatform("windows")]
public static class StableLauncherBootstrap
{
  internal enum BootstrapPoint { AfterCopy, AfterRecord }

  public static InstallationRecord Ensure(InstallationLayout layout, JournalSnapshot snapshot,
    AtomicStateStore state, string installationRecordPath) =>
    Ensure(layout, snapshot, state, installationRecordPath, null);

  internal static InstallationRecord Ensure(InstallationLayout layout, JournalSnapshot snapshot,
    AtomicStateStore state, string installationRecordPath, Action<BootstrapPoint>? fault)
  {
    var decision = LaunchResolver.Resolve(layout, snapshot);
    if (decision.Kind != LaunchDecisionKind.Ready || decision.Target is not { Package: { } package })
      throw new InvalidDataException("Only the committed, verified package can bootstrap the launcher.");
    var installation = snapshot.Installation.Value!;
    var sourceFile = LauncherPayload(package) ??
      throw new InvalidDataException("The current package has no stable launcher payload.");
    using var sourceRoot = SafeDirectoryHandle.OpenPath(
      Path.GetDirectoryName(decision.Target.ExecutablePath)!, false);
    using var source = OpenPayload(sourceRoot, decision.Target, sourceFile);
    FileIdentity identity;
    using (var destinationRoot = SafeDirectoryHandle.OpenPath(layout.ManagedRoot.Path, true))
    {
      if (destinationRoot.Identity != layout.ManagedRoot.Identity)
        throw new InvalidDataException("The managed root changed.");
      if (installation.StableLauncherIdentity is { } recorded)
      {
        using var existing = destinationRoot.OpenFile("BookSplice.Launcher.exe");
        SafeDirectoryHandle.RejectReparseFile(existing);
        if (NativeFileIdentity.Read(existing) != recorded ||
            !string.Equals(PackageInventory.HashHandle(existing),
              installation.StableLauncherSha256, StringComparison.OrdinalIgnoreCase))
          throw new InvalidDataException("The stable launcher changed.");
        return installation;
      }
      using var created = destinationRoot.CreateNewFile("BookSplice.Launcher.exe");
      identity = CopyPayload(source, created, sourceFile);
    }
    var afterCopy = LaunchResolver.Resolve(layout, snapshot);
    if (afterCopy.Kind != LaunchDecisionKind.Ready ||
        !LaunchResolver.SameTarget(afterCopy.Target, decision.Target))
      throw new InvalidDataException("The committed package changed during launcher bootstrap: " +
        afterCopy.Reason);
    fault?.Invoke(BootstrapPoint.AfterCopy);
    RevalidateCopy(layout, identity, sourceFile.Sha256);
    var updated = installation with
    {
      StableLauncherIdentity = identity,
      StableLauncherSha256 = sourceFile.Sha256
    };
    state.Transition<InstallationRecord>(installationRecordPath, read =>
    {
      if (read.Kind != StateReadKind.Valid || read.Value is not { } current ||
          current.InstallationId != installation.InstallationId ||
          current.ManagedRoot.Identity != installation.ManagedRoot.Identity ||
          current.OriginalPackageBindingSha256 != installation.OriginalPackageBindingSha256 ||
          current.StableLauncherIdentity is not null)
        throw new InvalidDataException("The installation record changed during launcher bootstrap.");
      return updated;
    });
    fault?.Invoke(BootstrapPoint.AfterRecord);
    RevalidateCopy(layout, identity, sourceFile.Sha256);
    return updated;
  }

  // Replaces the stable launcher with the committed package's copy when the two differ, so launcher
  // fixes reach installations that already have one. Returns false when nothing was replaced; a
  // launcher that is still running blocks the rename and the next start tries again.
  public static bool Refresh(OpenedInstallation opened)
  {
    ArgumentNullException.ThrowIfNull(opened);
    var layout = opened.Layout;
    using var admission = new ActivityLeaseStore(layout.DataRoot).TryEnterUpdate();
    if (admission is null) return false;
    var snapshot = JournalSnapshot.Read(opened.State, layout.Records.Path);
    if (snapshot.Installation.Value is not { } installation ||
        installation.StableLauncherIdentity is not { } oldIdentity ||
        installation.StableLauncherSha256 is not { } oldSha256)
      return false;
    var decision = LaunchResolver.Resolve(layout, snapshot);
    if (decision.Kind != LaunchDecisionKind.Ready || decision.Target is not { Package: { } package } ||
        LauncherPayload(package) is not { } sourceFile ||
        string.Equals(sourceFile.Sha256, oldSha256, StringComparison.OrdinalIgnoreCase))
      return false;
    RevalidateCopy(layout, oldIdentity, oldSha256);
    using var sourceRoot = SafeDirectoryHandle.OpenPath(
      Path.GetDirectoryName(decision.Target.ExecutablePath)!, false);
    using var source = OpenPayload(sourceRoot, decision.Target, sourceFile);
    FileIdentity identity;
    using (var root = SafeDirectoryHandle.OpenPath(layout.ManagedRoot.Path, false,
      layout.ManagedRoot.Path, layout.ManagedRoot.Identity, allowEntryMutation: true))
    {
      if (root.Identity != layout.ManagedRoot.Identity)
        throw new InvalidDataException("The managed root changed.");
      var tempPath = Path.Combine(layout.ManagedRoot.Path, RefreshTempName);
      File.Delete(tempPath);
      using (var created = root.CreateStateTemp(RefreshTempName))
      {
        identity = CopyPayload(source, created, sourceFile);
        try { SafeDirectoryHandle.RenameStateTemp(created, root, "BookSplice.Launcher.exe"); }
        catch (System.ComponentModel.Win32Exception)
        {
          created.Dispose();
          File.Delete(tempPath);
          return false;
        }
      }
    }
    opened.State.Transition<InstallationRecord>(
      Path.Combine(opened.RecordsPath, "installation.json"), read =>
        read.Kind == StateReadKind.Valid && read.Value is { } current &&
        current.InstallationId == installation.InstallationId &&
        current.StableLauncherIdentity == oldIdentity &&
        string.Equals(current.StableLauncherSha256, oldSha256, StringComparison.OrdinalIgnoreCase) ?
          current with { StableLauncherIdentity = identity, StableLauncherSha256 = sourceFile.Sha256 } :
          throw new InvalidDataException("The installation record changed during launcher refresh."));
    RevalidateCopy(layout, identity, sourceFile.Sha256);
    return true;
  }

  internal const string RefreshTempName = "BookSplice.Launcher.exe.new";

  // The launcher payload of the committed package, which a refresh may already have installed
  // before it could record the new identity.
  internal static PackageFile? CommittedPayload(AtomicStateStore state, string recordsPath) =>
    state.Read<CurrentTarget>(Path.Combine(recordsPath, "current.json")).Value?.Current?.Package is { } package ?
      LauncherPayload(package) : null;

  private static PackageFile? LauncherPayload(PersistedPackage package) =>
    package.Files.SingleOrDefault(file =>
      string.Equals(file.RelativePath, "BookSplice.Launcher.exe", StringComparison.Ordinal));

  private static SafeFileHandle OpenPayload(SafeDirectoryHandle sourceRoot,
    OwnedOrOriginalTarget target, PackageFile sourceFile)
  {
    if (sourceRoot.Identity != target.DirectoryIdentity)
      throw new InvalidDataException("The current package root changed.");
    var source = sourceRoot.OpenFile(sourceFile.RelativePath);
    try
    {
      SafeDirectoryHandle.RejectReparseFile(source);
      if (NativeFileIdentity.Read(source) != sourceFile.Identity ||
          RandomAccess.GetLength(source) != sourceFile.Length ||
          !string.Equals(PackageInventory.HashHandle(source), sourceFile.Sha256,
            StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The current launcher payload changed.");
      return source;
    }
    catch { source.Dispose(); throw; }
  }

  private static FileIdentity CopyPayload(SafeFileHandle source, SafeFileHandle created,
    PackageFile sourceFile)
  {
    var buffer = new byte[128 * 1024];
    long offset = 0;
    while (offset < sourceFile.Length)
    {
      var count = RandomAccess.Read(source, buffer, offset);
      if (count == 0) throw new EndOfStreamException("The launcher payload was truncated.");
      RandomAccess.Write(created, buffer.AsSpan(0, count), offset);
      offset += count;
    }
    RandomAccess.FlushToDisk(created);
    if (!string.Equals(PackageInventory.HashHandle(created), sourceFile.Sha256,
          StringComparison.OrdinalIgnoreCase))
      throw new IOException("The stable launcher copy changed during creation.");
    return NativeFileIdentity.Read(created);
  }

  // Starts the recorded stable launcher after checking the image against the installation record.
  public static SupervisedProcess Start(InstallationLayout layout, InstallationRecord installation,
    IReadOnlyList<string> arguments)
  {
    RequireRecorded(layout, installation);
    using var launcher = VerifiedExecutable.PinFile(layout.StableLauncherPath);
    if (launcher.Identity != installation.StableLauncherIdentity)
      throw new InvalidDataException("The stable launcher was replaced.");
    return ProcessSupervisor.Start(launcher, arguments, new Dictionary<string, string>());
  }

  internal static void RequireRecorded(InstallationLayout layout, InstallationRecord installation)
  {
    if (installation.StableLauncherIdentity is not { } identity ||
        installation.StableLauncherSha256 is not { } sha256)
      throw new InvalidDataException("The stable launcher has not been installed.");
    RevalidateCopy(layout, identity, sha256);
  }

  private static void RevalidateCopy(InstallationLayout layout, FileIdentity identity,
    string sha256)
  {
    using var root = SafeDirectoryHandle.OpenPath(layout.ManagedRoot.Path, false);
    if (root.Identity != layout.ManagedRoot.Identity)
      throw new InvalidDataException("The managed launcher root changed.");
    using var file = root.OpenFile("BookSplice.Launcher.exe");
    SafeDirectoryHandle.RejectReparseFile(file);
    if (NativeFileIdentity.Read(file) != identity ||
        !string.Equals(PackageInventory.HashHandle(file), sha256,
          StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("The copied launcher was replaced.");
  }
}
