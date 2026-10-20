using System.Runtime.Versioning;

namespace BookSplice.Updates.Storage;

[SupportedOSPlatform("windows")]
public sealed class CurrentTargetStore
{
  private readonly AtomicStateStore _state;
  private readonly UpdateJournal _journal;
  private readonly string _path;
  private readonly InstallationLayout _layout;
  public CurrentTargetStore(AtomicStateStore state, UpdateJournal journal,
    InstallationLayout layout)
  {
    _state = state ?? throw new ArgumentNullException(nameof(state));
    _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    _path = Path.Combine(layout.Records.Path, "current.json");
  }

  public CurrentTarget CapturePriorMapping(OwnedOrOriginalTarget expectedOld)
  {
    ArgumentNullException.ThrowIfNull(expectedOld);
    var read = _state.Read<CurrentTarget>(_path);
    if (read.Kind != StateReadKind.Valid || read.Value is not { } mapping ||
        mapping.InstallationId != _layout.InstallationId ||
        !LaunchResolver.SameTarget(mapping.Current, expectedOld))
      throw new InvalidDataException("The pre-update launch mapping changed.");
    return mapping;
  }

  public CurrentTarget Commit(Guid operationId, PackageInventory retainedCandidate)
  {
    ArgumentNullException.ThrowIfNull(retainedCandidate);
    var transaction = RequireTransaction(operationId, Handover.UpdatePhase.Committing);
    var oldTarget = transaction.OldTarget;
    var prior = transaction.PriorMapping ??
      throw new InvalidDataException("The pre-update launch mapping was not recorded.");
    var newTarget = transaction.NewTarget ??
      throw new InvalidDataException("The committed update has no candidate target.");
    ValidateNewTarget(newTarget, transaction, retainedCandidate);
    return _state.Transition<CurrentTarget>(_path, read =>
    {
      if (read.Kind != StateReadKind.Valid || read.Value is not { } current ||
          current.InstallationId != _layout.InstallationId ||
          !UpdateTransaction.SameMapping(current, prior) ||
          !LaunchResolver.SameTarget(current.Current, oldTarget))
        throw new InvalidDataException("The current launch target changed before commit.");
      return new CurrentTarget(1, _layout.InstallationId, newTarget, oldTarget);
    });
  }

  public CurrentTarget Restore(Guid operationId)
  {
    var transaction = RequireTransaction(operationId, Handover.UpdatePhase.RollingBack);
    var oldTarget = transaction.OldTarget;
    var newTarget = transaction.NewTarget;
    var prior = transaction.PriorMapping ??
      throw new InvalidDataException("The pre-update launch mapping was not recorded.");
    return _state.Transition<CurrentTarget>(_path, read =>
    {
      if (read.Kind != StateReadKind.Valid || read.Value is not { } current ||
          current.InstallationId != _layout.InstallationId ||
          newTarget is null || !LaunchResolver.SameTarget(current.Current, newTarget) ||
          !LaunchResolver.SameTarget(current.Previous, oldTarget))
        throw new InvalidDataException("The committed candidate mapping changed before restore.");
      return prior;
    });
  }

  private UpdateTransaction RequireTransaction(Guid id, Handover.UpdatePhase phase)
  {
    if (id == Guid.Empty) throw new ArgumentException("Operation ID is empty.", nameof(id));
    var read = _journal.Read();
    if (read.Kind != StateReadKind.Valid || read.Value is not { } transaction ||
        transaction.Id != id || transaction.InstallationId != _layout.InstallationId ||
        transaction.Phase != phase)
      throw new InvalidDataException("The update operation or phase changed.");
    return transaction;
  }

  private void ValidateNewTarget(OwnedOrOriginalTarget target,
    UpdateTransaction transaction, PackageInventory retainedCandidate)
  {
    if (target.Kind != TargetKind.Managed || target.OwnedDirectory is not { } owned ||
        transaction.Promotion?.Promoted != owned ||
        target.Package is null || retainedCandidate.Destination != owned ||
        !string.Equals(Path.GetDirectoryName(owned.Path), _layout.Versions.Path,
          StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("The candidate is not the recorded promoted version.");
    retainedCandidate.Revalidate();
    var snapshot = retainedCandidate.Snapshot();
    if (target.Package.Sha256 != snapshot.Sha256 ||
        target.Package.RootIdentity != snapshot.RootIdentity ||
        !string.Equals(target.ExecutablePath,
          Path.Combine(owned.Path, "BookSplice.Gui.exe"),
          StringComparison.OrdinalIgnoreCase) ||
        !snapshot.Files.Any(file => file.RelativePath == "BookSplice.Gui.exe" &&
          file.Identity == target.ExecutableIdentity))
      throw new InvalidDataException("The retained candidate differs from its recorded target.");
    retainedCandidate.ValidatePackage(target.Version);
  }
}
