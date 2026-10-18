using BookSplice.Updates.Handover;
using System.Runtime.Versioning;

namespace BookSplice.Updates.Storage;

public enum ProcessLife { Alive, Exited, Unknown }
public enum PointerAction { Keep, RestoreOld }
public enum OldAction { None, ResumeExisting, LaunchAfterValidation, ManualRecoveryOnly }
public enum RecoveryStop { None, Candidate, OldGracefully }

public sealed class ProcessSnapshot
{
  private readonly System.Collections.ObjectModel.ReadOnlyDictionary<ProcessIdentity, ProcessLife> _states;

  public ProcessSnapshot(IReadOnlyDictionary<ProcessIdentity, ProcessLife> states)
  {
    ArgumentNullException.ThrowIfNull(states);
    if (states.Any(pair => pair.Key is null || !Enum.IsDefined(pair.Value)))
      throw new ArgumentException("Process state evidence is invalid.", nameof(states));
    _states = new System.Collections.ObjectModel.ReadOnlyDictionary<ProcessIdentity, ProcessLife>(
      new Dictionary<ProcessIdentity, ProcessLife>(states));
  }

  public ProcessLife For(ProcessIdentity identity) => _states.TryGetValue(identity, out var life) ?
    life : ProcessLife.Unknown;
}

public sealed record JournalSnapshot(StateRead<InstallationRecord> Installation,
  StateRead<CurrentTarget> Current, StateRead<UpdateTransaction> Transaction,
  StateRead<OwnershipState> Ownership);

[SupportedOSPlatform("windows")]
public sealed record RecoveryDecision(bool Refused, string? Reason,
  OwnedOrOriginalTarget? TargetPendingNativeValidation, PointerAction MappingAction,
  RecoveryStop Stop, ProcessIdentity? ExactProcess, bool WaitForObservedExit, OldAction Old,
  bool CandidateWritePermissionAfterValidation)
{
  public PointerAction AfterObservedExitMappingAction { get; init; } = PointerAction.Keep;

  public static RecoveryDecision Refuse(string reason) =>
    new(true, reason, null, PointerAction.Keep, RecoveryStop.None, null, false,
      OldAction.None, false);
}

[SupportedOSPlatform("windows")]
public static class RecoveryPolicy
{
  public static RecoveryDecision Decide(JournalSnapshot snapshot, ProcessSnapshot processes)
  {
    if (snapshot.Installation.Kind != StateReadKind.Valid ||
        snapshot.Installation.Value is null || !snapshot.Installation.Value.IsValid() ||
        snapshot.Ownership.Kind != StateReadKind.Valid || snapshot.Ownership.Value is null ||
        snapshot.Transaction.Kind == StateReadKind.Invalid)
      return RecoveryDecision.Refuse("Installation, ownership, or transaction state is invalid.");

    var installation = snapshot.Installation.Value;
    var ownership = snapshot.Ownership.Value;
    if (ownership.InstallationId != installation.InstallationId ||
        ownership.RecordRootIdentity != installation.ManagedRoot.Identity)
      return RecoveryDecision.Refuse("Ownership is bound to another installation.");

    var current = snapshot.Current.Value;
    var transaction = snapshot.Transaction.Value;
    if (snapshot.Current.Kind == StateReadKind.Invalid)
    {
      if (snapshot.Transaction.Kind != StateReadKind.Missing)
        return RecoveryDecision.Refuse("The current mapping is corrupt during a recorded transaction.");
      var original = new OwnedOrOriginalTarget(TargetKind.Original,
        installation.OriginalExecutablePath, installation.OriginalIdentity,
        installation.OriginalExecutableIdentity, installation.OriginalPackage,
        installation.OriginalVersion,
        installation.OriginalProtocolVersion, null);
      if (!TargetBelongs(original, installation, ownership))
        return RecoveryDecision.Refuse("The recorded original target is invalid.");
      return new RecoveryDecision(false, null, original, PointerAction.Keep,
        RecoveryStop.None, null, false, FreshAction(original), false);
    }
    if (snapshot.Transaction.Kind == StateReadKind.Missing)
    {
      if (current is null || !TargetBelongs(current.Current, installation, ownership) ||
          (current.Previous is not null &&
           !TargetBelongs(current.Previous, installation, ownership)))
        return RecoveryDecision.Refuse("No validated launch mapping is recorded.");
      return new RecoveryDecision(false, null, current.Current, PointerAction.Keep,
        RecoveryStop.None, null, false, FreshAction(current.Current), false);
    }
    if (transaction is null || transaction.InstallationId != installation.InstallationId ||
        !TargetBelongs(transaction.OldTarget, installation, ownership) ||
        (transaction.NewTarget is not null &&
         !TargetBelongs(transaction.NewTarget, installation, ownership)) ||
        (transaction.Promotion is not null &&
         (!string.Equals(transaction.Promotion.DestinationParent.Path,
           installation.Versions.Path, StringComparison.OrdinalIgnoreCase) ||
          !string.Equals(transaction.Promotion.Source.Path,
            Path.Combine(installation.Staging.Path, transaction.Id.ToString("D"), "payload"),
            StringComparison.OrdinalIgnoreCase))) ||
        (transaction.Coordinator is not null &&
         !string.Equals(transaction.Coordinator.ExecutablePath,
           installation.StableLauncherPath, StringComparison.OrdinalIgnoreCase)) ||
        (transaction.OldProcess is not null &&
         !string.Equals(transaction.OldProcess.ExecutablePath,
           transaction.OldTarget.ExecutablePath, StringComparison.OrdinalIgnoreCase)) ||
        (transaction.NewProcess is not null &&
         (transaction.NewTarget is null ||
          !string.Equals(transaction.NewProcess.ExecutablePath,
            transaction.NewTarget.ExecutablePath, StringComparison.OrdinalIgnoreCase))))
      return RecoveryDecision.Refuse("The transaction target is not bound to this installation.");

    if (transaction.Coordinator is not null &&
        processes.For(transaction.Coordinator) != ProcessLife.Exited)
      return RecoveryDecision.Refuse("The recorded coordinator must be resolved first.");

    if (transaction.Phase == UpdatePhase.Committed)
    {
      if (current is null || transaction.NewTarget is null ||
          !Same(current.Current, transaction.NewTarget) ||
          (current.Previous is null || !Same(current.Previous, transaction.OldTarget)))
        return RecoveryDecision.Refuse("The committed launch mapping is inconsistent.");
      if (transaction.OldProcess is not null &&
          processes.For(transaction.OldProcess) == ProcessLife.Unknown)
        return RecoveryDecision.Refuse("The old process identity is unresolved.");
      if (transaction.OldProcess is not null &&
          processes.For(transaction.OldProcess) == ProcessLife.Alive)
        return new RecoveryDecision(false, null, transaction.NewTarget,
          PointerAction.Keep, RecoveryStop.OldGracefully, transaction.OldProcess,
          true, OldAction.None, false);
      return new RecoveryDecision(false, null, transaction.NewTarget,
        PointerAction.Keep, RecoveryStop.None, null, false, OldAction.None, true);
    }

    if (transaction.Phase is not (UpdatePhase.Preparing or UpdatePhase.Prepared or
      UpdatePhase.Starting or UpdatePhase.Ready or UpdatePhase.Committing or
      UpdatePhase.RollingBack or UpdatePhase.Failed))
      return RecoveryDecision.Refuse("The transaction phase is unknown.");

    if (current is null || (!Same(current.Current, transaction.OldTarget) &&
        (transaction.NewTarget is null || !Same(current.Current, transaction.NewTarget))))
      return RecoveryDecision.Refuse("The provisional launch mapping is inconsistent.");
    if (transaction.NewProcess is not null &&
        processes.For(transaction.NewProcess) == ProcessLife.Unknown)
      return RecoveryDecision.Refuse("The candidate process identity is unresolved.");
    if (transaction.OldProcess is not null &&
        processes.For(transaction.OldProcess) == ProcessLife.Unknown)
      return RecoveryDecision.Refuse("The old process identity is unresolved.");

    var pointer = Same(current.Current, transaction.OldTarget) ? PointerAction.Keep :
      PointerAction.RestoreOld;
    if (transaction.NewProcess is not null &&
        processes.For(transaction.NewProcess) == ProcessLife.Alive)
      return new RecoveryDecision(false, null, transaction.OldTarget, PointerAction.Keep,
        RecoveryStop.Candidate, transaction.NewProcess, true, OldAction.None, false)
      { AfterObservedExitMappingAction = pointer };
    var oldAction = transaction.OldProcess is not null &&
      processes.For(transaction.OldProcess) == ProcessLife.Alive ?
      OldAction.ResumeExisting : FreshAction(transaction.OldTarget);
    return new RecoveryDecision(false, null, transaction.OldTarget, pointer,
      RecoveryStop.None, oldAction == OldAction.ResumeExisting ? transaction.OldProcess : null,
      false, oldAction, false);
  }

  private static bool Same(OwnedOrOriginalTarget a, OwnedOrOriginalTarget b) =>
    a.Kind == b.Kind && a.DirectoryIdentity == b.DirectoryIdentity &&
    a.ExecutableIdentity == b.ExecutableIdentity && a.ProtocolVersion == b.ProtocolVersion &&
    string.Equals(a.ExecutablePath, b.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
    a.Version == b.Version && a.OwnedDirectory == b.OwnedDirectory &&
    ((a.Package is null && b.Package is null) ||
      (a.Package is not null && b.Package is not null &&
       a.Package.RootIdentity == b.Package.RootIdentity &&
       string.Equals(a.Package.Sha256, b.Package.Sha256, StringComparison.OrdinalIgnoreCase)));

  private static OldAction FreshAction(OwnedOrOriginalTarget target) =>
    target.Kind == TargetKind.Original && target.Package is null ?
      OldAction.ManualRecoveryOnly : OldAction.LaunchAfterValidation;

  private static bool TargetBelongs(OwnedOrOriginalTarget target,
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
