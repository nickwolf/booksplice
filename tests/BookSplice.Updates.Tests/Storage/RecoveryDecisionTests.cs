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

  private static OwnedDirectory Owned(string path, FileIdentity identity, string kind) =>
    new(path, identity, InstallationId, OperationId, kind);

  private static (InstallationRecord Installation, OwnershipState Ownership,
    OwnedOrOriginalTarget Old, OwnedOrOriginalTarget New) Fixture()
  {
    var root = Owned(Managed, ManagedIdentity, "managed-root");
    var records = Owned(Managed + @"\records", ManagedIdentity, "records");
    var versions = Owned(Managed + @"\versions", ManagedIdentity, "versions");
    var staging = Owned(Managed + @"\staging", ManagedIdentity, "staging");
    var trash = Owned(Managed + @"\trash", ManagedIdentity, "trash");
    var version = Owned(Managed + @"\versions\1.1.0", ManagedIdentity, "version");
    var package = PersistedPackage.FromSnapshot(new PackageSnapshot(ManagedIdentity, [],
      [new PackageFile("BookSplice.Gui.exe", 1, new string('a', 64), ExecutableIdentity)]));
    var installation = new InstallationRecord(1, InstallationId, Root, OriginalIdentity,
      @"C:\BookSpliceData", OriginalIdentity, root, records, versions, staging, trash,
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
    var prior = new CurrentTarget(1, InstallationId, fixture.Old, null);
    var transaction = new UpdateTransaction(1, InstallationId, OperationId, phase,
      fixture.Old, fixture.New, null, true, 1, new string('a', 64), [], null, null, prior);
    var current = newPointer ? new CurrentTarget(1, InstallationId, fixture.New, fixture.Old) :
      prior;
    return new(new(StateReadKind.Valid, fixture.Installation),
      new(StateReadKind.Valid, current), new(StateReadKind.Valid, transaction),
      new(StateReadKind.Valid, fixture.Ownership));
  }

  [Theory]
  [InlineData(UpdatePhase.Preparing)]
  [InlineData(UpdatePhase.Prepared)]
  [InlineData(UpdatePhase.Starting)]
  public void UnfinishedTransactionIsNeverResolvedByPolicy(UpdatePhase phase)
  {
    var decision = RecoveryPolicy.Decide(Snapshot(phase, false));
    Assert.True(decision.Refused);
    Assert.Null(decision.TargetPendingNativeValidation);
  }

  [Fact]
  public void MissingTransactionUsesRecordedCurrentPendingNativeValidation()
  {
    var snapshot = Snapshot(UpdatePhase.Committed, true) with
    { Transaction = new StateRead<UpdateTransaction>(StateReadKind.Missing) };
    var decision = RecoveryPolicy.Decide(snapshot);
    Assert.False(decision.Refused);
    Assert.Equal(Fixture().New.ExecutablePath,
      decision.TargetPendingNativeValidation!.ExecutablePath);
  }

  [Fact]
  public void UnownedManagedTargetRefuses()
  {
    var snapshot = Snapshot(UpdatePhase.Committed, true) with
    {
      Transaction = new StateRead<UpdateTransaction>(StateReadKind.Missing),
      Ownership = new StateRead<OwnershipState>(StateReadKind.Valid,
        new OwnershipState(1, InstallationId, ManagedIdentity, []))
    };
    Assert.True(RecoveryPolicy.Decide(snapshot).Refused);
  }

  [Fact]
  public void InvalidTransactionRefusesLaunchAndCleanup()
  {
    var snapshot = Snapshot(UpdatePhase.Committed, true) with
    { Transaction = new StateRead<UpdateTransaction>(StateReadKind.Invalid) };
    var decision = RecoveryPolicy.Decide(snapshot);
    Assert.True(decision.Refused);
    Assert.Null(decision.TargetPendingNativeValidation);
  }

  [Fact]
  public void CorruptCurrentWithoutTransactionFallsBackToRecordedOriginal()
  {
    var snapshot = Snapshot(UpdatePhase.Committed, true) with
    {
      Current = new StateRead<CurrentTarget>(StateReadKind.Invalid),
      Transaction = new StateRead<UpdateTransaction>(StateReadKind.Missing)
    };
    var decision = RecoveryPolicy.Decide(snapshot);
    Assert.False(decision.Refused);
    Assert.Equal(TargetKind.Original, decision.TargetPendingNativeValidation!.Kind);
    Assert.Equal(Root + @"\BookSplice.Gui.exe",
      decision.TargetPendingNativeValidation.ExecutablePath);
  }
}
