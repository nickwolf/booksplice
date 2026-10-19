using System.Runtime.Versioning;
using BookSplice.Updates.Storage;

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
    var sourceFile = package.Files.SingleOrDefault(file =>
      string.Equals(file.RelativePath, "BookSplice.Launcher.exe", StringComparison.Ordinal));
    if (sourceFile is null)
      throw new InvalidDataException("The current package has no stable launcher payload.");
    using var sourceRoot = SafeDirectoryHandle.OpenPath(
      Path.GetDirectoryName(decision.Target.ExecutablePath)!, false);
    if (sourceRoot.Identity != decision.Target.DirectoryIdentity)
      throw new InvalidDataException("The current package root changed.");
    using var source = sourceRoot.OpenFile(sourceFile.RelativePath);
    SafeDirectoryHandle.RejectReparseFile(source);
    if (NativeFileIdentity.Read(source) != sourceFile.Identity ||
        RandomAccess.GetLength(source) != sourceFile.Length ||
        !string.Equals(PackageInventory.HashHandle(source), sourceFile.Sha256,
          StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("The current launcher payload changed.");
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
      identity = NativeFileIdentity.Read(created);
      if (!string.Equals(PackageInventory.HashHandle(created), sourceFile.Sha256,
            StringComparison.OrdinalIgnoreCase))
        throw new IOException("The stable launcher copy changed during creation.");
    }
    var afterCopy = LaunchResolver.Resolve(layout, snapshot);
    if (afterCopy.Kind != LaunchDecisionKind.Ready ||
        !LaunchResolver.SameTarget(afterCopy.Target, decision.Target))
      throw new InvalidDataException("The committed package changed during launcher bootstrap.");
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
