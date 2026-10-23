using System.Diagnostics;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Handover;

[Collection("WindowsOwnedStorage")]
public sealed class LaunchHandoverTests
{
  [Fact]
  public async Task UpdateCommitsAfterReadyAndCandidateWritesOnlyAfterCommit()
  {
    using var h = new HandoverHarness();
    var settings = File.ReadAllBytes(h.SettingsPath);
    var id = await h.PrepareUpdateAsync();
    var prepared = h.Transaction!;
    Assert.Equal(UpdatePhase.Prepared, prepared.Phase);
    Assert.Equal(h.Admission.Entered, h.Admission.Released);
    Assert.True(UpdateTransaction.SameMapping(prepared.PriorMapping, h.Mapping));
    var old = h.StartOld();
    _ = Task.Delay(500).ContinueWith(_ => old.StopAsync());
    var markerBeforeCommit = true;
    var mappingBeforeCommit = false;
    var outcome = await h.RunAsync(h.Handover(afterReady: () =>
    {
      markerBeforeCommit = File.Exists(h.MarkerPath);
      mappingBeforeCommit = LaunchResolver.SameTarget(h.OldTarget, h.Mapping!.Current);
    }), id, old.Identity);

    Assert.True(outcome.Kind == UpdateOutcomeKind.Committed, outcome.Reason);
    Assert.True(old.HasExited);
    Assert.False(markerBeforeCommit);
    Assert.True(mappingBeforeCommit);
    Assert.True(LaunchResolver.SameTarget(prepared.NewTarget, h.Mapping!.Current));
    Assert.True(LaunchResolver.SameTarget(h.OldTarget, h.Mapping.Previous));
    Assert.Equal(UpdatePhase.Committed, h.Transaction!.Phase);
    Assert.Equal(h.Candidates.Single(), h.Transaction.NewProcess);
    await h.WaitForMarkerAsync();
    Assert.Equal(settings, File.ReadAllBytes(h.SettingsPath));
    Assert.Empty(h.FallbackLaunches);
    Assert.Equal(h.Admission.Entered, h.Admission.Released);
    var resolved = LaunchResolver.Resolve(h.Layout,
      JournalSnapshot.Read(h.State, h.Layout.Records.Path));
    Assert.Equal(LaunchDecisionKind.Ready, resolved.Kind);
    Assert.True(LaunchResolver.SameTarget(prepared.NewTarget, resolved.Target));
  }

  [Fact]
  public async Task PointerSwapWithoutCommittedRecordFinishesAsCommitted()
  {
    using var h = new HandoverHarness();
    var id = await h.PrepareUpdateAsync();
    var prepared = h.Transaction!;
    var armed = false;
    var writes = 0;
    h.WriteFault = point =>
    {
      if (armed && point == StateWritePoint.BeforeReplace && ++writes == 2)
        throw new IOException("Injected crash before the Committed record.");
    };
    var outcome = await h.RunAsync(h.Handover(afterReady: () => armed = true), id,
      await h.ExitedOldAsync());

    Assert.Equal(UpdateOutcomeKind.Committed, outcome.Kind);
    Assert.True(outcome.FallbackLaunched);
    Assert.Equal(UpdatePhase.Committed, h.Transaction!.Phase);
    Assert.True(LaunchResolver.SameTarget(prepared.NewTarget, h.Mapping!.Current));
    Assert.True(LaunchResolver.SameTarget(h.OldTarget, h.Mapping.Previous));
    Assert.True(h.CandidateExited(0));
    var fallback = Assert.Single(h.FallbackLaunches);
    Assert.Equal(LaunchDecisionKind.Ready, fallback.Kind);
    Assert.True(LaunchResolver.SameTarget(prepared.NewTarget, fallback.Target));
  }

  [Fact]
  public async Task RecoveryCommitsATransactionWhosePointerAlreadyMoved()
  {
    using var h = new HandoverHarness();
    var id = await h.PrepareUpdateAsync();
    var prepared = h.Transaction!;
    var armed = false;
    var writes = 0;
    h.WriteFault = point =>
    {
      if (armed && point == StateWritePoint.BeforeReplace && ++writes >= 2)
        throw new IOException("Injected crash after the pointer moved.");
    };
    var outcome = await h.RunAsync(h.Handover(afterReady: () => armed = true), id,
      await h.ExitedOldAsync());

    Assert.Equal(UpdateOutcomeKind.Failed, outcome.Kind);
    Assert.Equal(UpdatePhase.Starting, h.Transaction!.Phase);
    Assert.True(LaunchResolver.SameTarget(prepared.NewTarget, h.Mapping!.Current));
    Assert.True(h.CandidateExited(0));

    h.WriteFault = null;
    var recovery = new RecoveryCoordinator(h.Journal, h.Admission, h.Targets)
    {
      EndCandidate = _ => true
    };
    Assert.Equal(RecoveryOutcomeKind.Recovered, recovery.Recover());
    Assert.Equal(UpdatePhase.Committed, h.Transaction!.Phase);
    var resolved = LaunchResolver.Resolve(h.Layout,
      JournalSnapshot.Read(h.State, h.Layout.Records.Path));
    Assert.Equal(LaunchDecisionKind.Ready, resolved.Kind);
    Assert.True(LaunchResolver.SameTarget(prepared.NewTarget, resolved.Target));
  }

  [Fact]
  public async Task FailuresBeforeCommitKeepTheMappingAndLaunchTheMappedTarget()
  {
    using var h = new HandoverHarness();
    var settings = File.ReadAllText(h.SettingsPath);
    const string changed = "{\"schemaVersion\":1,\"changed\":true}";
    var scenarios = new (string Name, Func<LaunchHandover> Create, bool SettingsChanged)[]
    {
      ("no ready", () => h.Handover(r => h.Candidate(r, "hold"),
        readiness: TimeSpan.FromMilliseconds(500)), false),
      ("early exit", () => h.Handover(r => h.Candidate(r, "exit")), false),
      ("wrong nonce", () => h.Handover(r => h.Candidate(r with
      {
        Nonce = Convert.ToBase64String(UpdatePipe.NewNonce())
      })), false),
      ("late ready", () => h.Handover(r => h.Candidate(r, delayMilliseconds: 1500),
        readiness: TimeSpan.FromMilliseconds(300)), false),
      ("settings changed", () => h.Handover(
        afterReady: () => File.WriteAllText(h.SettingsPath, changed)), true)
    };
    foreach (var scenario in scenarios)
    {
      File.WriteAllText(h.SettingsPath, settings);
      var id = await h.PrepareUpdateAsync();
      var prior = h.Transaction!.PriorMapping;
      var launches = h.FallbackLaunches.Count;
      var timer = Stopwatch.StartNew();
      var outcome = await h.RunAsync(scenario.Create(), id, await h.ExitedOldAsync());

      Assert.True(outcome.Kind == UpdateOutcomeKind.Failed, scenario.Name);
      Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), scenario.Name);
      Assert.True(outcome.FallbackLaunched, scenario.Name + ": " + outcome.Reason);
      Assert.Equal(UpdatePhase.Failed, h.Transaction!.Phase);
      Assert.True(UpdateTransaction.SameMapping(prior, h.Mapping), scenario.Name);
      Assert.True(h.CandidateExited(h.Candidates.Count - 1), scenario.Name);
      Assert.False(File.Exists(h.MarkerPath), scenario.Name);
      Assert.Equal(launches + 1, h.FallbackLaunches.Count);
      Assert.Equal(LaunchDecisionKind.Ready, h.FallbackLaunches.Last().Kind);
      Assert.True(LaunchResolver.SameTarget(h.OldTarget, h.FallbackLaunches.Last().Target));
      Assert.Equal(scenario.SettingsChanged ? changed : settings, File.ReadAllText(h.SettingsPath));
      Assert.Equal(h.Admission.Entered, h.Admission.Released);
    }
  }

  [Fact]
  public async Task CandidateThatCannotBeStoppedBlocksTheFallbackLaunch()
  {
    using var h = new HandoverHarness();
    var id = await h.PrepareUpdateAsync();
    var outcome = await h.RunAsync(h.Handover(r => h.Candidate(r, "hold"),
      stop: _ => Task.FromException(new IOException("Injected stop failure.")),
      readiness: TimeSpan.FromMilliseconds(300)), id, await h.ExitedOldAsync());

    Assert.Equal(UpdateOutcomeKind.Failed, outcome.Kind);
    Assert.False(outcome.FallbackLaunched);
    Assert.Empty(h.FallbackLaunches);
    Assert.Equal(UpdatePhase.Failed, h.Transaction!.Phase);
    Assert.False(h.CandidateExited(0));
    await h.StopCandidatesAsync();
  }

  [Fact]
  public async Task OldGuiThatDoesNotExitStartsNothing()
  {
    using var h = new HandoverHarness();
    var id = await h.PrepareUpdateAsync();
    var prior = h.Transaction!.PriorMapping;
    using var old = h.StartOld();
    var outcome = await h.RunAsync(h.Handover(oldExit: TimeSpan.FromMilliseconds(300)), id,
      old.Identity);

    Assert.Equal(UpdateOutcomeKind.Failed, outcome.Kind);
    Assert.False(outcome.FallbackLaunched);
    Assert.Empty(h.Candidates);
    Assert.Empty(h.FallbackLaunches);
    Assert.Equal(UpdatePhase.Failed, h.Transaction!.Phase);
    Assert.True(UpdateTransaction.SameMapping(prior, h.Mapping));
    Assert.False(old.HasExited);
    await old.StopAsync();
  }

  [Fact]
  public async Task AdmissionHeldByAJobFailsAndLaunchesTheMappedTarget()
  {
    using var h = new HandoverHarness();
    var id = await h.PrepareUpdateAsync();
    var outcome = await h.RunAsync(h.Handover(activity: new TrackingLeaseStore(() => true)), id,
      await h.ExitedOldAsync());

    Assert.Equal(UpdateOutcomeKind.Failed, outcome.Kind);
    Assert.True(outcome.FallbackLaunched);
    Assert.Empty(h.Candidates);
    Assert.Equal(UpdatePhase.Failed, h.Transaction!.Phase);
    Assert.True(LaunchResolver.SameTarget(h.OldTarget, Assert.Single(h.FallbackLaunches).Target));
  }

  [Fact]
  public async Task LostFailureRecordNeverThrowsOrLaunchesTwice()
  {
    using var h = new HandoverHarness();
    var id = await h.PrepareUpdateAsync();
    var recovered = false;
    var held = new TrackingLeaseStore(() =>
    {
      if (!recovered) h.Journal.Transition(id, UpdatePhase.Prepared, UpdatePhase.Failed);
      recovered = true;
      return true;
    });
    var admission = await h.RunAsync(h.Handover(activity: held), id, await h.ExitedOldAsync());
    Assert.Equal(UpdateOutcomeKind.Failed, admission.Kind);
    Assert.False(admission.FallbackLaunched);
    Assert.Empty(h.FallbackLaunches);

    id = await h.PrepareUpdateAsync();
    using var old = h.StartOld();
    _ = Task.Delay(200).ContinueWith(_ =>
      h.Journal.Transition(id, UpdatePhase.Prepared, UpdatePhase.Failed));
    var exit = await h.RunAsync(h.Handover(oldExit: TimeSpan.FromMilliseconds(800)), id,
      old.Identity);
    Assert.Equal(UpdateOutcomeKind.Failed, exit.Kind);
    Assert.False(exit.FallbackLaunched);
    await old.StopAsync();

    id = await h.PrepareUpdateAsync();
    var handover = await h.RunAsync(h.Handover(afterReady: () =>
      h.Journal.Transition(id, UpdatePhase.Starting, UpdatePhase.Failed)), id,
      await h.ExitedOldAsync());
    Assert.Equal(UpdateOutcomeKind.Failed, handover.Kind);
    Assert.True(handover.FallbackLaunched);
    Assert.True(h.CandidateExited(0));
    Assert.True(LaunchResolver.SameTarget(h.OldTarget, h.Mapping!.Current));
    Assert.Single(h.FallbackLaunches);
  }
}
