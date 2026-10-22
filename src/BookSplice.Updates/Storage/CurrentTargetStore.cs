using System.Runtime.Versioning;
using BookSplice.Updates.Windows;

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

  public CurrentTarget? Read() =>
    _state.Read<CurrentTarget>(_path) is { Kind: StateReadKind.Valid, Value: { } mapping } &&
    mapping.InstallationId == _layout.InstallationId ? mapping : null;

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

  public CurrentTarget Commit(Guid operationId, VerifiedExecutable retainedCandidate)
  {
    ArgumentNullException.ThrowIfNull(retainedCandidate);
    var transaction = RequireTransaction(operationId, Handover.UpdatePhase.Starting);
    var newTarget = transaction.NewTarget ??
      throw new InvalidDataException("The handover has no candidate target.");
    retainedCandidate.Revalidate();
    if (retainedCandidate.Identity != newTarget.ExecutableIdentity ||
        !string.Equals(retainedCandidate.Path, newTarget.ExecutablePath,
          StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("The retained candidate differs from its recorded target.");
    return _state.Transition<CurrentTarget>(_path, read =>
    {
      if (read.Kind != StateReadKind.Valid || read.Value is not { } current ||
          !UpdateTransaction.SameMapping(current, transaction.PriorMapping))
        throw new InvalidDataException("The current launch target changed before commit.");
      return new CurrentTarget(1, _layout.InstallationId, newTarget, transaction.OldTarget);
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
}
