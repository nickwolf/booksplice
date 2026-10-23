using System.Runtime.Versioning;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Storage;

public enum LaunchDecisionKind { Ready, RecoveryRequired, Refused }

[SupportedOSPlatform("windows")]
public sealed record LaunchDecision(LaunchDecisionKind Kind, OwnedOrOriginalTarget? Target,
  string? Reason)
{
  public static LaunchDecision Refuse(string reason) =>
    new(LaunchDecisionKind.Refused, null, reason);
}

[SupportedOSPlatform("windows")]
public static class LaunchResolver
{
  internal static IReadOnlyDictionary<string, string> ChildEnvironment(string dataRoot,
    FileIdentity dataIdentity)
  {
    if (!string.Equals(Path.GetFileName(dataRoot), "BookSplice",
          StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(Path.GetFullPath(dataRoot), dataRoot,
          StringComparison.OrdinalIgnoreCase) || !dataIdentity.IsValid)
      throw new InvalidDataException("The recorded data root cannot be passed to the GUI.");
    var parentPath = Path.GetDirectoryName(dataRoot) ??
      throw new InvalidDataException("The recorded data root has no parent.");
    using var parent = SafeDirectoryHandle.OpenPath(parentPath, false);
    using var child = SafeDirectoryHandle.OpenGuardedChild(parent, "BookSplice");
    if (child.Identity != dataIdentity)
      throw new InvalidDataException("The recorded data root was replaced.");
    return new Dictionary<string, string>
    {
      ["BOOKSPLICE_LOCAL_APP_DATA"] = parentPath
    };
  }

  internal static bool SameTarget(OwnedOrOriginalTarget? left, OwnedOrOriginalTarget? right) =>
    left is not null && right is not null && left.Kind == right.Kind &&
    left.DirectoryIdentity == right.DirectoryIdentity &&
    left.ExecutableIdentity == right.ExecutableIdentity &&
    left.Version == right.Version && left.ProtocolVersion == right.ProtocolVersion &&
    string.Equals(left.ExecutablePath, right.ExecutablePath,
      StringComparison.OrdinalIgnoreCase) &&
    left.Package?.Sha256 == right.Package?.Sha256 &&
    left.OwnedDirectory == right.OwnedDirectory;

  public static LaunchDecision Resolve(InstallationLayout layout, JournalSnapshot snapshot)
  {
    ArgumentNullException.ThrowIfNull(layout);
    ArgumentNullException.ThrowIfNull(snapshot);
    try
    {
      if (!string.Equals(layout.Records.Path,
            Path.Combine(layout.ManagedRoot.Path, "records"), StringComparison.OrdinalIgnoreCase))
        return LaunchDecision.Refuse("The records directory is outside the installation.");
      using (var managedRoot = SafeDirectoryHandle.OpenPath(layout.ManagedRoot.Path, false))
      using (var records = SafeDirectoryHandle.OpenGuardedChild(managedRoot, "records"))
      {
        if (managedRoot.Identity != layout.ManagedRoot.Identity ||
            records.Identity != layout.Records.Identity)
          return LaunchDecision.Refuse("The records directory changed.");
      }
      if (snapshot.Installation.Kind != StateReadKind.Valid ||
          snapshot.Installation.Value is not { } installation || !installation.IsValid() ||
          installation.InstallationId != layout.InstallationId ||
          installation.OriginalIdentity != layout.OriginalIdentity ||
          installation.DataIdentity != layout.DataIdentity ||
          installation.ManagedRoot != layout.ManagedRoot ||
          installation.Records != layout.Records ||
          snapshot.Ownership.Kind != StateReadKind.Valid ||
          snapshot.Ownership.Value is not { } ownership ||
          ownership.InstallationId != layout.InstallationId ||
          !OwnershipState.Definition("ownership.json", layout.Records,
            layout.ManagedRoot).Validate(ownership))
        return LaunchDecision.Refuse("The installation or ownership record is invalid.");
      if (snapshot.Transaction.Kind == StateReadKind.Invalid)
        return LaunchDecision.Refuse("The update transaction is invalid.");
      if (snapshot.Transaction.Kind == StateReadKind.Valid)
      {
        if (snapshot.Transaction.Value is null || !snapshot.Transaction.Value.IsValid())
          return LaunchDecision.Refuse("The update transaction is invalid.");
        if (!snapshot.Transaction.Value.IsFinished())
          return new LaunchDecision(LaunchDecisionKind.RecoveryRequired, null,
            "A recorded update requires recovery.");
      }
      if (snapshot.Current.Kind == StateReadKind.Missing ||
          snapshot.Current.Kind == StateReadKind.Valid &&
          (snapshot.Current.Value is not { } recorded ||
           recorded.InstallationId != layout.InstallationId ||
           recorded.Current is null || !recorded.Current.IsValid()))
        return LaunchDecision.Refuse("No committed launch target is available.");
      var policy = RecoveryPolicy.Decide(snapshot);
      if (policy.Refused || policy.TargetPendingNativeValidation is not { } target ||
          snapshot.Current.Value is { } current && target != current.Current)
        return LaunchDecision.Refuse(policy.Reason ?? "The committed mapping is inconsistent.");
      if (target.ProtocolVersion != BookSplice.Updates.Handover.UpdateProtocol.ProtocolVersion)
        return LaunchDecision.Refuse("This application requires a manual upgrade.");
      if (target.Kind == TargetKind.Managed && (target.OwnedDirectory is not { } owned ||
          !ownership.Entries.Any(entry => entry.Path.Equals(owned.Path,
              StringComparison.OrdinalIgnoreCase) && entry.Identity == owned.Identity &&
              entry.Kind == "version" && entry.UpdaterCreatedMarker == OwnershipEntry.Marker)))
        return LaunchDecision.Refuse("The managed target is not owned by this installation.");
      using var executable = OpenTarget(layout, installation, target);
      return new LaunchDecision(LaunchDecisionKind.Ready, target, null);
    }
    catch (Exception error) when (error is IOException or InvalidDataException or
      UnauthorizedAccessException or ArgumentException or NotSupportedException or
      System.ComponentModel.Win32Exception)
    {
      return LaunchDecision.Refuse("Native launch validation failed: " + error.Message);
    }
  }

  internal static VerifiedExecutable OpenTarget(InstallationLayout layout,
    InstallationRecord installation, OwnedOrOriginalTarget target)
  {
    if (target.Kind == TargetKind.Original)
    {
      if (target.Package is null || installation.OriginalPackage is null ||
          target.Package.Sha256 != installation.OriginalPackage.Sha256 ||
          target.DirectoryIdentity != installation.OriginalIdentity ||
          target.ExecutableIdentity != installation.OriginalExecutableIdentity ||
          !string.Equals(target.ExecutablePath, installation.OriginalExecutablePath,
            StringComparison.OrdinalIgnoreCase) ||
          installation.OriginalParentIdentity is not { } parent)
        throw new InvalidDataException("The original target is not bound to this installation.");
      var inventory = OriginalPackageInventory.Reopen(layout.OriginalApplicationPath,
        parent, target.Package, target.Version);
      try { return VerifiedExecutable.FromOriginal(inventory, target); }
      catch { inventory.Dispose(); throw; }
    }
    if (target.Kind != TargetKind.Managed || target.OwnedDirectory is not { } owned ||
        target.Package is null || owned.InstallationId != layout.InstallationId ||
        !string.Equals(Path.GetDirectoryName(owned.Path), layout.Versions.Path,
          StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("The managed target is not owned by this installation.");
    var store = new OwnedDirectoryStore(Path.GetDirectoryName(layout.OriginalApplicationPath)!,
      _ => throw new InvalidOperationException("Launch resolution cannot create directories."));
    var package = PackageInventory.ReopenSnapshot(owned, target.Package.Restore(), store, false);
    try { return VerifiedExecutable.FromManaged(package, target); }
    catch { package.Dispose(); throw; }
  }

  public static LaunchDecision LaunchCurrent(InstallationLayout layout, AtomicStateStore state)
  {
    ArgumentNullException.ThrowIfNull(layout);
    ArgumentNullException.ThrowIfNull(state);
    var decision = Resolve(layout, JournalSnapshot.Read(state, layout.Records.Path));
    if (decision.Kind != LaunchDecisionKind.Ready) return decision;
    using var child = StartVerified(layout, JournalSnapshot.Read(state, layout.Records.Path),
      decision, [], ChildEnvironment(layout.DataRoot, layout.DataIdentity));
    return decision;
  }

  internal static SupervisedProcess StartVerified(InstallationLayout layout,
    JournalSnapshot snapshot, LaunchDecision decision,
    IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environment)
  {
    if (decision.Kind != LaunchDecisionKind.Ready || decision.Target is not { } target ||
        args.Count != 0)
      throw new InvalidOperationException("Only a validated normal launch is allowed.");
    var again = Resolve(layout, snapshot);
    if (again.Kind != LaunchDecisionKind.Ready || !SameTarget(again.Target, target))
      throw new InvalidDataException("The committed launch target changed.");
    using var executable = OpenTarget(layout, snapshot.Installation.Value!, target);
    return ProcessSupervisor.Start(executable, args, environment);
  }
}
