using System.Runtime.Versioning;

namespace BookSplice.Updates.Storage;

public sealed record JournalSnapshot(StateRead<InstallationRecord> Installation,
  StateRead<CurrentTarget> Current, StateRead<UpdateTransaction> Transaction,
  StateRead<OwnershipState> Ownership);

[SupportedOSPlatform("windows")]
public sealed record RecoveryDecision(bool Refused, string? Reason,
  OwnedOrOriginalTarget? TargetPendingNativeValidation)
{
  public static RecoveryDecision Refuse(string reason) => new(true, reason, null);
}

[SupportedOSPlatform("windows")]
public static class RecoveryPolicy
{
  public static RecoveryDecision Decide(JournalSnapshot snapshot)
  {
    if (snapshot.Installation.Kind != StateReadKind.Valid ||
        snapshot.Installation.Value is null || !snapshot.Installation.Value.IsValid() ||
        snapshot.Ownership.Kind != StateReadKind.Valid || snapshot.Ownership.Value is null ||
        snapshot.Transaction.Kind == StateReadKind.Invalid)
      return RecoveryDecision.Refuse("Installation, ownership, or transaction state is invalid.");

    var installation = snapshot.Installation.Value;
    var ownership = snapshot.Ownership.Value;
    if (ownership.InstallationId != installation.InstallationId ||
        !OwnershipState.Definition("ownership.json", installation.Records,
          installation.ManagedRoot).Validate(ownership))
      return RecoveryDecision.Refuse("Ownership is bound to another installation.");
    if (snapshot.Transaction.Kind != StateReadKind.Missing)
      return RecoveryDecision.Refuse("A recorded update transaction must be resolved first.");

    if (snapshot.Current.Kind == StateReadKind.Invalid)
    {
      var original = new OwnedOrOriginalTarget(TargetKind.Original,
        installation.OriginalExecutablePath, installation.OriginalIdentity,
        installation.OriginalExecutableIdentity, installation.OriginalPackage,
        installation.OriginalVersion, installation.OriginalProtocolVersion, null);
      if (!TargetBelongs(original, installation, ownership))
        return RecoveryDecision.Refuse("The recorded original target is invalid.");
      return new RecoveryDecision(false, null, original);
    }
    var current = snapshot.Current.Value;
    if (current is null || !TargetBelongs(current.Current, installation, ownership) ||
        (current.Previous is not null &&
         !TargetBelongs(current.Previous, installation, ownership)))
      return RecoveryDecision.Refuse("No validated launch mapping is recorded.");
    return new RecoveryDecision(false, null, current.Current);
  }

  internal static bool TargetBelongs(OwnedOrOriginalTarget target,
    InstallationRecord installation, OwnershipState ownership)
  {
    if (!target.IsValid()) return false;
    if (target.Kind == TargetKind.Original)
      return target.DirectoryIdentity == installation.OriginalIdentity &&
        target.ExecutableIdentity == installation.OriginalExecutableIdentity &&
        ((target.Package is null && installation.OriginalPackage is null) ||
         (target.Package is not null && installation.OriginalPackage is not null &&
          target.Package.RootIdentity == installation.OriginalPackage.RootIdentity &&
          string.Equals(target.Package.Sha256, installation.OriginalPackage.Sha256,
            StringComparison.OrdinalIgnoreCase))) &&
        target.Version == installation.OriginalVersion &&
        target.ProtocolVersion == installation.OriginalProtocolVersion &&
        string.Equals(target.ExecutablePath, installation.OriginalExecutablePath,
          StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetDirectoryName(target.ExecutablePath),
          installation.OriginalApplicationPath, StringComparison.OrdinalIgnoreCase);
    var owned = target.OwnedDirectory!;
    return owned.InstallationId == installation.InstallationId &&
      string.Equals(Path.GetDirectoryName(owned.Path), installation.Versions.Path,
        StringComparison.OrdinalIgnoreCase) &&
      ownership.Entries.Any(x => x.InstallationId == installation.InstallationId &&
        x.Identity == owned.Identity &&
        string.Equals(x.Path, owned.Path, StringComparison.OrdinalIgnoreCase));
  }
}
