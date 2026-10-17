using BookSplice.Updates.Handover;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Storage;

public sealed class RecoveryDecisionTests
{
  private static readonly Guid InstallationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
  private static readonly Guid OperationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
  private static readonly FileIdentity OriginalIdentity = new(1, "11111111111111111111111111111111");
  private static readonly FileIdentity ManagedIdentity = new(1, "22222222222222222222222222222222");
  private static readonly FileIdentity ExecutableIdentity = new(1, "33333333333333333333333333333333");
  private const string Root = @"C:\BookSplice";
  private const string Managed = @"C:\.booksplice-managed-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
  private static readonly ProcessIdentity CandidateProcess = new(42, 100,
    Managed + @"\versions\1.1.0\BookSplice.Gui.exe");
  private static readonly ProcessIdentity OldProcess = new(43, 101,
    Root + @"\BookSplice.Gui.exe");

  private static OwnedDirectory Owned(string path, FileIdentity identity, string kind) =>
    new(path, identity, InstallationId, OperationId, kind);

  private static (InstallationRecord Installation, OwnershipState Ownership,
    OwnedOrOriginalTarget Old, OwnedOrOriginalTarget New) Fixture()
  {
    var root = Owned(Managed, ManagedIdentity, "managed-root");
    var versions = Owned(Managed + @"\versions", ManagedIdentity, "versions");
    var staging = Owned(Managed + @"\staging", ManagedIdentity, "staging");
    var trash = Owned(Managed + @"\trash", ManagedIdentity, "trash");
    var version = Owned(Managed + @"\versions\1.1.0", ManagedIdentity, "version");
    var package = PersistedPackage.FromSnapshot(new PackageSnapshot(ManagedIdentity, [],
      [new PackageFile("BookSplice.Gui.exe", 1, new string('a', 64), ExecutableIdentity)]));
    var installation = new InstallationRecord(1, InstallationId, Root, OriginalIdentity,
      @"C:\BookSpliceData", OriginalIdentity, root, versions, staging, trash,
      Managed + @"\BookSplice.Launcher.exe", null, Root + @"\BookSplice.Gui.exe",
      ExecutableIdentity, new string('a', 64), "1.0.0", 1);
    var ownership = new OwnershipState(1, InstallationId, ManagedIdentity,
      [OwnershipEntry.FromCreated(version)]);
    var old = new OwnedOrOriginalTarget(TargetKind.Original, Root + @"\BookSplice.Gui.exe",
      OriginalIdentity, ExecutableIdentity, null, "1.0.0", 1, null);
    var next = new OwnedOrOriginalTarget(TargetKind.Managed,
      Managed + @"\versions\1.1.0\BookSplice.Gui.exe", ManagedIdentity,
      ExecutableIdentity, package, "1.1.0", 1, version);
    return (installation, ownership, old, next);
  }

  private static JournalSnapshot Snapshot(UpdatePhase phase, bool newPointer)
  {
    var fixture = Fixture();
    var transaction = new UpdateTransaction(1, InstallationId, OperationId, phase,
      fixture.Old, fixture.New, null, true, 1, new string('a', 64), [], null,
      OldProcess, CandidateProcess, null);
    var current = new CurrentTarget(1, InstallationId,
      newPointer ? fixture.New : fixture.Old, newPointer ? fixture.Old : null);
    return new(new(StateReadKind.Valid, fixture.Installation),
      new(StateReadKind.Valid, current), new(StateReadKind.Valid, transaction),
      new(StateReadKind.Valid, fixture.Ownership));
  }

  [Fact]
  public void PointerChangedBeforeCommitRecordRollsBackAfterExactCandidateExit()
  {
    var decision = RecoveryPolicy.Decide(Snapshot(UpdatePhase.Committing, true),
      new ProcessSnapshot(new Dictionary<ProcessIdentity, ProcessLife>
      {
        [CandidateProcess] = ProcessLife.Alive,
        [OldProcess] = ProcessLife.Alive
      }));
    Assert.False(decision.Refused);
    Assert.Equal(PointerAction.Keep, decision.MappingAction);
    Assert.Equal(PointerAction.RestoreOld, decision.AfterObservedExitMappingAction);
    Assert.Equal(RecoveryStop.Candidate, decision.Stop);
    Assert.Equal(CandidateProcess, decision.ExactProcess);
    Assert.True(decision.WaitForObservedExit);
    Assert.Equal(OldAction.None, decision.Old);
    Assert.False(decision.CandidateWritePermissionAfterValidation);
  }

  [Theory]
  [InlineData(UpdatePhase.Preparing)]
  [InlineData(UpdatePhase.Prepared)]
  [InlineData(UpdatePhase.Starting)]
  [InlineData(UpdatePhase.Ready)]
  [InlineData(UpdatePhase.Committing)]
  [InlineData(UpdatePhase.RollingBack)]
  [InlineData(UpdatePhase.Failed)]
  public void EveryProvisionalPhaseRequiresCandidateExitBeforePointerRestore(UpdatePhase phase)
  {
    var snapshot = Snapshot(phase, true);
    var alive = RecoveryPolicy.Decide(snapshot, new ProcessSnapshot(
      new Dictionary<ProcessIdentity, ProcessLife>
      { [CandidateProcess] = ProcessLife.Alive, [OldProcess] = ProcessLife.Alive }));
    Assert.False(alive.Refused);
    Assert.Equal(RecoveryStop.Candidate, alive.Stop);
    Assert.Equal(PointerAction.Keep, alive.MappingAction);
    Assert.Equal(PointerAction.RestoreOld, alive.AfterObservedExitMappingAction);
    Assert.True(alive.WaitForObservedExit);
    Assert.Equal(OldAction.None, alive.Old);
    Assert.False(alive.CandidateWritePermissionAfterValidation);

    var exited = RecoveryPolicy.Decide(snapshot, new ProcessSnapshot(
      new Dictionary<ProcessIdentity, ProcessLife>
      { [CandidateProcess] = ProcessLife.Exited, [OldProcess] = ProcessLife.Alive }));
    Assert.False(exited.Refused);
    Assert.Equal(PointerAction.RestoreOld, exited.MappingAction);
    Assert.Equal(OldAction.ResumeExisting, exited.Old);
    Assert.Equal(OldProcess, exited.ExactProcess);
    Assert.False(exited.CandidateWritePermissionAfterValidation);
  }

  [Theory]
  [InlineData(ProcessLife.Alive)]
  [InlineData(ProcessLife.Unknown)]
  public void UnsettledCoordinatorRefusesDuplicateRecovery(ProcessLife life)
  {
    var snapshot = Snapshot(UpdatePhase.Committing, true);
    var coordinator = new ProcessIdentity(45, 200,
      Managed + @"\BookSplice.Launcher.exe");
    snapshot = snapshot with
    {
      Transaction = snapshot.Transaction with
      { Value = snapshot.Transaction.Value! with { Coordinator = coordinator } }
    };
    var decision = RecoveryPolicy.Decide(snapshot, new ProcessSnapshot(
      new Dictionary<ProcessIdentity, ProcessLife>
      {
        [coordinator] = life,
        [CandidateProcess] = ProcessLife.Exited,
        [OldProcess] = ProcessLife.Exited
      }));
    Assert.True(decision.Refused);
  }

  [Fact]
  public void CommittedOldUnknownNeverEnablesCandidateWrites()
  {
    var decision = RecoveryPolicy.Decide(Snapshot(UpdatePhase.Committed, true),
      new ProcessSnapshot(new Dictionary<ProcessIdentity, ProcessLife>
      { [CandidateProcess] = ProcessLife.Alive }));
    Assert.True(decision.Refused);
    Assert.False(decision.CandidateWritePermissionAfterValidation);
  }

  [Fact]
  public void ContradictoryCurrentVersionRefusesPromotion()
  {
    var snapshot = Snapshot(UpdatePhase.Committed, true);
    snapshot = snapshot with
    {
      Current = snapshot.Current with
      {
        Value = snapshot.Current.Value! with
        { Current = snapshot.Current.Value.Current with { Version = "9.9.9" } }
      }
    };
    var decision = RecoveryPolicy.Decide(snapshot, new ProcessSnapshot(
      new Dictionary<ProcessIdentity, ProcessLife>
      { [CandidateProcess] = ProcessLife.Alive, [OldProcess] = ProcessLife.Exited }));
    Assert.True(decision.Refused);
  }

  [Fact]
  public void MissingCandidateInPreparingCanResumeRecordedOld()
  {
    var snapshot = Snapshot(UpdatePhase.Preparing, false);
    snapshot = snapshot with
    {
      Transaction = snapshot.Transaction with
      { Value = snapshot.Transaction.Value! with { NewTarget = null, NewProcess = null } }
    };
    var decision = RecoveryPolicy.Decide(snapshot, new ProcessSnapshot(
      new Dictionary<ProcessIdentity, ProcessLife> { [OldProcess] = ProcessLife.Alive }));
    Assert.False(decision.Refused);
    Assert.Equal(RecoveryStop.None, decision.Stop);
    Assert.Equal(OldAction.ResumeExisting, decision.Old);
  }

  [Fact]
  public void ExitedOriginalRequiresManualRecoveryUntilFullInventoryExists()
  {
    var decision = RecoveryPolicy.Decide(Snapshot(UpdatePhase.Committing, true),
      new ProcessSnapshot(new Dictionary<ProcessIdentity, ProcessLife>
      { [CandidateProcess] = ProcessLife.Exited, [OldProcess] = ProcessLife.Exited }));
    Assert.False(decision.Refused);
    Assert.Equal(OldAction.ManualRecoveryOnly, decision.Old);
    Assert.Equal(PointerAction.RestoreOld, decision.MappingAction);
  }

  [Fact]
  public void ProcessEvidenceIsCopiedBeforeUse()
  {
    var evidence = new Dictionary<ProcessIdentity, ProcessLife>
    { [CandidateProcess] = ProcessLife.Alive };
    var snapshot = new ProcessSnapshot(evidence);
    evidence[CandidateProcess] = ProcessLife.Exited;
    Assert.Equal(ProcessLife.Alive, snapshot.For(CandidateProcess));
  }

  [Fact]
  public void MissingIdentityStatusNeverMeansExited()
  {
    var decision = RecoveryPolicy.Decide(Snapshot(UpdatePhase.Committing, true),
      new ProcessSnapshot(new Dictionary<ProcessIdentity, ProcessLife>
      { [OldProcess] = ProcessLife.Alive }));
    Assert.True(decision.Refused);
  }

  [Fact]
  public void DurableCommitWaitsForOldExitBeforeCandidateWrites()
  {
    var decision = RecoveryPolicy.Decide(Snapshot(UpdatePhase.Committed, true),
      new ProcessSnapshot(new Dictionary<ProcessIdentity, ProcessLife>
      { [CandidateProcess] = ProcessLife.Alive, [OldProcess] = ProcessLife.Alive }));
    Assert.Equal(RecoveryStop.OldGracefully, decision.Stop);
    Assert.Equal(OldProcess, decision.ExactProcess);
    Assert.False(decision.CandidateWritePermissionAfterValidation);
  }

  [Fact]
  public void MissingTransactionUsesRecordedCurrentPendingNativeValidation()
  {
    var snapshot = Snapshot(UpdatePhase.Committed, true) with
    { Transaction = new StateRead<UpdateTransaction>(StateReadKind.Missing) };
    var decision = RecoveryPolicy.Decide(snapshot,
      new ProcessSnapshot(new Dictionary<ProcessIdentity, ProcessLife>()));
    Assert.False(decision.Refused);
    Assert.Equal(Fixture().New.ExecutablePath,
      decision.TargetPendingNativeValidation!.ExecutablePath);
  }

  [Fact]
  public void InvalidTransactionRefusesLaunchAndCleanup()
  {
    var snapshot = Snapshot(UpdatePhase.Committing, true) with
    { Transaction = new StateRead<UpdateTransaction>(StateReadKind.Invalid) };
    var decision = RecoveryPolicy.Decide(snapshot,
      new ProcessSnapshot(new Dictionary<ProcessIdentity, ProcessLife>()));
    Assert.True(decision.Refused);
    Assert.Null(decision.TargetPendingNativeValidation);
    Assert.Equal(RecoveryStop.None, decision.Stop);
  }

  [Fact]
  public void CorruptCurrentWithoutTransactionFallsBackToRecordedOriginal()
  {
    var snapshot = Snapshot(UpdatePhase.Committed, true) with
    {
      Current = new StateRead<CurrentTarget>(StateReadKind.Invalid),
      Transaction = new StateRead<UpdateTransaction>(StateReadKind.Missing)
    };
    var decision = RecoveryPolicy.Decide(snapshot,
      new ProcessSnapshot(new Dictionary<ProcessIdentity, ProcessLife>()));
    Assert.False(decision.Refused);
    Assert.Equal(TargetKind.Original, decision.TargetPendingNativeValidation!.Kind);
    Assert.Equal(Root + @"\BookSplice.Gui.exe",
      decision.TargetPendingNativeValidation.ExecutablePath);
    Assert.False(decision.CandidateWritePermissionAfterValidation);
  }
}
