using System.Runtime.Versioning;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Storage;

public sealed record PersistedPromotion(OwnedDirectory Source, OwnedDirectory DestinationParent,
  string DestinationLeaf, string DestinationPath, PersistedPackage Snapshot,
  OwnedDirectory? Promoted);

[SupportedOSPlatform("windows")]
public sealed record UpdateTransaction(int SchemaVersion, Guid InstallationId, Guid Id,
  UpdatePhase Phase, OwnedOrOriginalTarget OldTarget, OwnedOrOriginalTarget? NewTarget,
  string? SettingsSha256, bool SettingsAbsent, int ProtocolVersion, string Nonce,
  IReadOnlyList<OwnedDirectory> OwnedPaths, ProcessIdentity? Coordinator,
  ProcessIdentity? OldProcess, ProcessIdentity? NewProcess, PersistedPromotion? Promotion,
  WriterFinalization? Finalization = null)
{
  public bool IsValid() => SchemaVersion == 1 && InstallationId != Guid.Empty && Id != Guid.Empty &&
    Enum.IsDefined(Phase) && OldTarget is not null && OldTarget.IsValid() &&
    (NewTarget is null || NewTarget.IsValid()) &&
    (SettingsAbsent ? SettingsSha256 is null : RecoveryRecordValidation.Hash(SettingsSha256)) &&
    ProtocolVersion == UpdateProtocol.ProtocolVersion &&
    Nonce is { Length: 64 } && Nonce.All(Uri.IsHexDigit) &&
    OwnedPaths is not null && OwnedPaths.Count <= 20_000 &&
    OwnedPaths.All(x => x is not null && x.Identity.IsValid &&
      x.InstallationId == InstallationId && x.OperationId == Id &&
      RecoveryRecordValidation.Absolute(x.Path)) &&
    OwnedPaths.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == OwnedPaths.Count &&
    ValidProcess(Coordinator) && ValidProcess(OldProcess) && ValidProcess(NewProcess) &&
    (Promotion is null || ValidPromotion(Promotion)) &&
    ValidFinalization(Finalization, Phase, OldProcess, NewProcess) &&
    (Phase is UpdatePhase.Preparing or UpdatePhase.RollingBack or UpdatePhase.Failed ||
     NewTarget is not null || Promotion is not null);

  private bool ValidPromotion(PersistedPromotion promotion)
  {
    try
    {
      return promotion.Source is not null && promotion.DestinationParent is not null &&
        promotion.Snapshot is not null && promotion.Source.Identity == promotion.Snapshot.RootIdentity &&
        promotion.Source.InstallationId == InstallationId && promotion.Source.OperationId == Id &&
        promotion.DestinationParent.InstallationId == InstallationId &&
        RecoveryRecordValidation.Absolute(promotion.Source.Path) &&
        RecoveryRecordValidation.Absolute(promotion.DestinationParent.Path) &&
        SafeZipExtractor.IsSafeComponent(promotion.DestinationLeaf) &&
        string.Equals(promotion.DestinationPath,
          Path.Combine(promotion.DestinationParent.Path, promotion.DestinationLeaf),
          StringComparison.OrdinalIgnoreCase) &&
        (promotion.Promoted is null ||
          (promotion.Promoted.Identity == promotion.Source.Identity &&
           promotion.Promoted.InstallationId == InstallationId &&
           promotion.Promoted.OperationId == Id && promotion.Promoted.Kind == "version" &&
           string.Equals(promotion.Promoted.Path, promotion.DestinationPath,
             StringComparison.OrdinalIgnoreCase))) &&
        promotion.Snapshot.Restore() is not null;
    }
    catch (Exception error) when (error is InvalidDataException or ArgumentException or NotSupportedException)
    { return false; }
  }

  private static bool ValidProcess(ProcessIdentity? process) => process is null ||
    (process.Pid > 0 && process.StartTimeUtcTicks > 0 &&
     RecoveryRecordValidation.Absolute(process.ExecutablePath));

  private static bool ValidFinalization(WriterFinalization? finalization, UpdatePhase phase,
    ProcessIdentity? oldProcess, ProcessIdentity? newProcess) => finalization is null ||
    (Enum.IsDefined(finalization.State) && finalization.ObservedAtUtcTicks > 0 &&
     finalization.Process is not null && ValidProcess(finalization.Process) &&
     (phase, finalization.State) switch
     {
       (UpdatePhase.Committed, WriterFinalizationState.CandidateActiveAfterOldExit) =>
         finalization.Process == newProcess,
       (UpdatePhase.Failed, WriterFinalizationState.OldRestoredAfterCandidateExit) =>
         finalization.Process == oldProcess,
       _ => false
     });

  public static StateRecordDefinition<UpdateTransaction> Definition(string filename,
    Guid installationId) => new(filename, value => value.InstallationId == installationId &&
      value.IsValid(), 16 * 1024 * 1024);
}

public enum WriterFinalizationState { CandidateActiveAfterOldExit, OldRestoredAfterCandidateExit }

public sealed record WriterFinalization(WriterFinalizationState State, long ObservedAtUtcTicks,
  ProcessIdentity Process);

[SupportedOSPlatform("windows")]
public sealed class UpdateJournal : IPromotionJournal
{
  private readonly AtomicStateStore _store;
  private readonly string _path;
  private readonly Guid _installationId;
  private readonly DurableOwnershipRecorder? _ownership;

  public UpdateJournal(AtomicStateStore store, string path, Guid installationId,
    DurableOwnershipRecorder? ownership = null)
  {
    _store = store ?? throw new ArgumentNullException(nameof(store));
    _path = path ?? throw new ArgumentNullException(nameof(path));
    _installationId = installationId != Guid.Empty ? installationId :
      throw new ArgumentException("Installation ID is empty.", nameof(installationId));
    _ownership = ownership;
  }

  public StateRead<UpdateTransaction> Read() => _store.Read<UpdateTransaction>(_path);

  public UpdateTransaction Begin(UpdateTransaction transaction)
  {
    ArgumentNullException.ThrowIfNull(transaction);
    if (transaction.InstallationId != _installationId || transaction.Phase != UpdatePhase.Preparing ||
        transaction.Finalization is not null)
      throw new InvalidDataException("The initial transaction is invalid.");
    return _store.Transition<UpdateTransaction>(_path, current =>
      current.Kind == StateReadKind.Missing ? transaction :
        throw new InvalidDataException("A prior update transaction still requires finalization."));
  }

  public UpdateTransaction Begin(UpdateTransaction transaction, Guid expectedPriorOperationId)
  {
    ArgumentNullException.ThrowIfNull(transaction);
    if (expectedPriorOperationId == Guid.Empty || transaction.Id == expectedPriorOperationId ||
        transaction.InstallationId != _installationId || transaction.Phase != UpdatePhase.Preparing ||
        transaction.Finalization is not null)
      throw new InvalidDataException("The replacement transaction is invalid.");
    return _store.Transition<UpdateTransaction>(_path, current =>
    {
      if (current.Kind != StateReadKind.Valid || current.Value is not { } prior ||
          prior.InstallationId != _installationId || prior.Id != expectedPriorOperationId ||
          !CanReplace(prior))
        throw new InvalidDataException("The exact prior transaction has not reached observed safe finalization.");
      return transaction;
    });
  }

  public UpdateTransaction RecordObservedFinalization(Guid id, UpdatePhase expected,
    WriterFinalizationState state, ProcessIdentity observedProcess)
  {
    ArgumentNullException.ThrowIfNull(observedProcess);
    return ChangeCore(id, expected, current => current with
    {
      Finalization = ValidateFinalization(current, expected, state, observedProcess)
    }, allowFinalization: true);
  }

  private static WriterFinalization ValidateFinalization(UpdateTransaction current,
    UpdatePhase expected, WriterFinalizationState state, ProcessIdentity observedProcess)
  {
    var matches = (expected, state) switch
    {
      (UpdatePhase.Committed, WriterFinalizationState.CandidateActiveAfterOldExit) =>
        current.NewProcess == observedProcess,
      (UpdatePhase.Failed, WriterFinalizationState.OldRestoredAfterCandidateExit) =>
        current.OldProcess == observedProcess,
      _ => false
    };
    if (!matches || !Enum.IsDefined(state) || observedProcess.Pid <= 0 ||
        observedProcess.StartTimeUtcTicks <= 0 ||
        !RecoveryRecordValidation.Absolute(observedProcess.ExecutablePath))
      throw new InvalidDataException("The writer finalization observation does not match a terminal safe state.");
    return new WriterFinalization(state, DateTime.UtcNow.Ticks, observedProcess);
  }

  private static bool CanReplace(UpdateTransaction transaction) => transaction.Finalization is { } final &&
    (transaction.Phase == UpdatePhase.Committed &&
       final.State == WriterFinalizationState.CandidateActiveAfterOldExit &&
       transaction.NewProcess == final.Process ||
     transaction.Phase == UpdatePhase.Failed &&
       final.State == WriterFinalizationState.OldRestoredAfterCandidateExit &&
       transaction.OldProcess == final.Process);

  public UpdateTransaction Transition(Guid id, UpdatePhase expected, UpdatePhase next) =>
    Change(id, expected, current =>
    {
      if (!Allowed(expected, next))
        throw new InvalidDataException("The journal phase transition is not allowed.");
      return current with { Phase = next };
    });

  public UpdateTransaction Change(Guid id, UpdatePhase expected,
    Func<UpdateTransaction, UpdateTransaction> change)
    => ChangeCore(id, expected, change, allowFinalization: false);

  private UpdateTransaction ChangeCore(Guid id, UpdatePhase expected,
    Func<UpdateTransaction, UpdateTransaction> change, bool allowFinalization)
  {
    ArgumentNullException.ThrowIfNull(change);
    if (id == Guid.Empty) throw new ArgumentException("Operation ID is empty.", nameof(id));
    return _store.Transition<UpdateTransaction>(_path, read =>
    {
      if (read.Kind != StateReadKind.Valid || read.Value is null ||
          read.Value.Id != id || read.Value.Phase != expected)
        throw new InvalidDataException("The journal operation or phase changed.");
      var next = change(read.Value);
      if (next.Id != id || next.InstallationId != _installationId || !next.IsValid() ||
          (next.Phase != expected && !Allowed(expected, next.Phase)) ||
          next.OldTarget != read.Value.OldTarget ||
          next.SettingsSha256 != read.Value.SettingsSha256 ||
          next.SettingsAbsent != read.Value.SettingsAbsent ||
          next.ProtocolVersion != read.Value.ProtocolVersion ||
          next.Nonce != read.Value.Nonce ||
          (!allowFinalization && next.Finalization != read.Value.Finalization) ||
          (allowFinalization && read.Value.Finalization is not null) ||
          (expected != UpdatePhase.Preparing && next.NewTarget != read.Value.NewTarget))
        throw new InvalidDataException("The journal mutation is invalid.");
      return next;
    });
  }

  public void RecordPromotionIntent(PromotionIntent intent)
  {
    ArgumentNullException.ThrowIfNull(intent);
    if (intent.InstallationId != _installationId || intent.OperationId == Guid.Empty)
      throw new InvalidDataException("The promotion belongs to another installation.");
    Change(intent.OperationId, UpdatePhase.Preparing, current =>
    {
      if (current.Promotion is not null) throw new InvalidDataException("Promotion already recorded.");
      return current with
      {
        Promotion = new PersistedPromotion(intent.Source,
        intent.DestinationParent, intent.DestinationLeaf, intent.DestinationPath,
        PersistedPackage.FromSnapshot(intent.Snapshot), null)
      };
    });
  }

  public void RecordPromotedDirectory(PromotionIntent intent, OwnedDirectory promoted)
  {
    ArgumentNullException.ThrowIfNull(intent);
    ArgumentNullException.ThrowIfNull(promoted);
    if (_ownership is null)
      throw new InvalidOperationException("Promotion requires a durable ownership recorder.");
    if (intent.InstallationId != _installationId ||
        promoted.InstallationId != intent.InstallationId ||
        promoted.OperationId != intent.OperationId || promoted.Kind != "version" ||
        promoted.Identity != intent.Source.Identity ||
        !string.Equals(promoted.Path, intent.DestinationPath, StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("The promoted directory differs from the intended destination.");
    var before = Read();
    if (before.Kind != StateReadKind.Valid || before.Value is null ||
        before.Value.Id != intent.OperationId || before.Value.Phase != UpdatePhase.Preparing ||
        !Matches(before.Value.Promotion, intent))
      throw new InvalidDataException("The promotion intent is not the active durable intent.");
    _ownership.Record(promoted);
    Change(intent.OperationId, UpdatePhase.Preparing, current =>
    {
      var recorded = current.Promotion;
      if (!Matches(recorded, intent))
        throw new InvalidDataException("The promoted package differs from its durable intent.");
      return current with { Promotion = recorded! with { Promoted = promoted } };
    });
  }

  private static bool Matches(PersistedPromotion? recorded, PromotionIntent intent) =>
    recorded is not null && recorded.Promoted is null &&
    recorded.Source == intent.Source && recorded.DestinationParent == intent.DestinationParent &&
    recorded.DestinationLeaf == intent.DestinationLeaf &&
    recorded.DestinationPath == intent.DestinationPath &&
    string.Equals(recorded.Snapshot.Sha256, intent.Snapshot.Sha256,
      StringComparison.OrdinalIgnoreCase) &&
    recorded.Snapshot.RootIdentity == intent.Snapshot.RootIdentity;

  private static bool Allowed(UpdatePhase from, UpdatePhase to) => (from, to) switch
  {
    (UpdatePhase.Preparing, UpdatePhase.Prepared) or
    (UpdatePhase.Prepared, UpdatePhase.Starting) or
    (UpdatePhase.Starting, UpdatePhase.Ready) or
    (UpdatePhase.Ready, UpdatePhase.Committing) or
    (UpdatePhase.Committing, UpdatePhase.Committed) or
    (UpdatePhase.Preparing or UpdatePhase.Prepared or UpdatePhase.Starting or
      UpdatePhase.Ready or UpdatePhase.Committing, UpdatePhase.RollingBack) or
    (UpdatePhase.RollingBack, UpdatePhase.Failed) => true,
    _ => false
  };
}
