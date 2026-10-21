using System.Diagnostics;
using System.Globalization;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Security;
using BookSplice.Updates.Transport;
using BookSplice.Updates.Tests.Storage;
using BookSplice.Updates.Tests.Windows;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Handover;

public sealed class UpdateCoordinatorTests
{
  private static readonly FileIdentity Identity = new(1, "0123456789ABCDEF0123456789ABCDEF");

  [Fact]
  public void ConfirmationRequiresExactReleaseIdentityAndForwardVersion()
  {
    var release = Candidate("v1.2.3");
    var identity = ReleaseSelector.Select([release]).Identity!;
    var old = new OwnedOrOriginalTarget(TargetKind.Original,
      @"C:\BookSplice\BookSplice.Gui.exe", Identity, Identity, null, "1.2.2", 1, null);
    var confirmation = new ConfirmedUpdate(release, identity, new string('a', 64),
      old, Guid.NewGuid());
    Assert.Null(UpdateCoordinator.CheckConfirmation(confirmation));
    Assert.Equal(UpdateOutcomeKind.Deferred,
      UpdateCoordinator.CheckConfirmation(confirmation with
      {
        ReleaseIdentity = new ReleaseIdentity(identity.Id, identity.Tag, identity.PageUri,
          "changed notes", identity.Assets)
      })!.Kind);
    Assert.Equal(UpdateOutcomeKind.Deferred,
      UpdateCoordinator.CheckConfirmation(confirmation with
      {
        OldTarget = old with { Version = "1.2.3" }
      })!.Kind);
    Assert.Equal(UpdateOutcomeKind.Deferred,
      UpdateCoordinator.CheckConfirmation(confirmation with
      {
        OldTarget = old with { Version = "1.3.0" }
      })!.Kind);
  }

  [Fact]
  public async Task ActiveJobDefersBeforeFreezingOldHost()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var fixture = new PackageStagerTests.Fixture();
    var release = Candidate("v1.2.3");
    var old = new OwnedOrOriginalTarget(TargetKind.Original,
      @"C:\BookSplice\BookSplice.Gui.exe", Identity, Identity, null, "1.2.2", 1, null);
    var update = new ConfirmedUpdate(release, ReleaseSelector.Select([release]).Identity!,
      new string('a', 64), old, Guid.NewGuid());
    var host = new RefusingOldHost();
    var coordinator = new UpdateCoordinator(null!, null!, null!, null!,
      new RefusedLeaseStore(), null!, null!, null!);

    var outcome = await coordinator.RunAsync(update, fixture.Layout, host,
      CancellationToken.None);

    Assert.Equal(UpdateOutcomeKind.Deferred, outcome.Kind);
    Assert.Empty(host.Events);
  }

  [Fact]
  public async Task AmbiguousOldExitRetainsSupervisionUntilNativeOldExitIsObserved()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var fixture = new PackageStagerTests.Fixture(runnableSelfCheck: true);
    var oldTarget = PrepareNativeOldTarget(fixture);
    var (stager, journal, state, _) = fixture.CreateDurableStager(beginJournal: false);
    var pointer = Path.Combine(fixture.Layout.Records.Path, "current.json");
    state.Write(pointer, new CurrentTarget(1, fixture.Layout.InstallationId, oldTarget, null));
    File.WriteAllText(Path.Combine(fixture.Layout.DataRoot, "settings.json"),
      "{\"schemaVersion\":1}");
    var current = new CurrentTargetStore(state, journal, fixture.Layout);
    var admission = new TrackingLeaseStore();
    using var coordinatorProcess = SupervisedProcess.CaptureCurrent();
    using var helper = VerifiedExecutable.PinFile(ProcessSupervisorTests.HelperPath());
    using var oldExecutable = VerifiedExecutable.PinFile(oldTarget.ExecutablePath);
    using var oldProcess = ProcessSupervisor.Start(oldExecutable, ["hold"],
      new Dictionary<string, string>());
    var host = new NativeOldHost(oldProcess, stopOnExit: false);
    SupervisedProcess? retainedCandidate = null;
    Process? candidateProcess = null;
    var coordinator = new UpdateCoordinator(new FixedManifestReader(fixture.Manifest),
      stager, journal, current, admission, fixture.Store, coordinatorProcess, oldProcess)
    {
      OldExitTimeout = TimeSpan.FromMilliseconds(200),
      CandidateLauncher = (_, request, _) =>
      {
        string[] args =
        [
          "pipe-ready", request.PipeName, request.TransactionId.ToString("D"),
          coordinatorProcess.Identity.Pid.ToString(CultureInfo.InvariantCulture),
          coordinatorProcess.Identity.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
          coordinatorProcess.Identity.ExecutablePath,
          Convert.ToHexString(Convert.FromBase64String(request.Nonce)),
          coordinatorProcess.ImageIdentity.VolumeSerial.ToString(CultureInfo.InvariantCulture),
          coordinatorProcess.ImageIdentity.FileIdHex, coordinatorProcess.ImageSha256
        ];
        retainedCandidate = ProcessSupervisor.Start(helper, args,
          new Dictionary<string, string>());
        candidateProcess = Process.GetProcessById(retainedCandidate.Identity.Pid);
        return retainedCandidate;
      }
    };
    var update = new ConfirmedUpdate(fixture.Release,
      ReleaseSelector.Select([fixture.Release]).Identity!, fixture.Manifest.ManifestSha256!,
      oldTarget, Guid.NewGuid());

    var wrongProcessCoordinator = new UpdateCoordinator(new FixedManifestReader(fixture.Manifest),
      stager, journal, current, admission, fixture.Store, coordinatorProcess, coordinatorProcess);
    var wrongProcessHost = new RefusingOldHost();
    var wrongProcess = await wrongProcessCoordinator.RunAsync(update, fixture.Layout,
      wrongProcessHost, CancellationToken.None);
    Assert.Equal(UpdateOutcomeKind.Deferred, wrongProcess.Kind);
    Assert.Empty(wrongProcessHost.Events);

    var freezeHost = new ThrowingFreezeHost();
    var freezeCoordinator = new UpdateCoordinator(new FixedManifestReader(fixture.Manifest),
      stager, journal, current, admission, fixture.Store, coordinatorProcess, oldProcess);
    var frozenException = await freezeCoordinator.RunAsync(update, fixture.Layout,
      freezeHost, CancellationToken.None);
    Assert.Equal(UpdateOutcomeKind.Failed, frozenException.Kind);
    Assert.Equal("freeze,restore", string.Join(',', freezeHost.Events));
    Assert.False(oldProcess.HasExited);
    using var freezeCancellation = new CancellationTokenSource();
    var cancelledFreezeHost = new ThrowingFreezeHost(freezeCancellation);
    var cancelledFreeze = await freezeCoordinator.RunAsync(update, fixture.Layout,
      cancelledFreezeHost, freezeCancellation.Token);
    Assert.Equal(UpdateOutcomeKind.Cancelled, cancelledFreeze.Kind);
    Assert.Equal("freeze,restore", string.Join(',', cancelledFreezeHost.Events));
    Assert.False(oldProcess.HasExited);
    try
    {
      var run = coordinator.RunAsync(update, fixture.Layout, host, CancellationToken.None);
      await host.ExitRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
      Assert.Equal(UpdatePhase.Committed, journal.Read().Value!.Phase);
      Assert.False(oldProcess.HasExited);
      Assert.NotNull(retainedCandidate);
      Assert.False(retainedCandidate.HasExited);
      await Task.Delay(350);
      Assert.IsType<TimeoutException>(await coordinator.UnresolvedFailure.WaitAsync(
        TimeSpan.FromSeconds(10)));
      await retainedCandidate.StopAsync();
      await Task.Delay(100);
      Assert.False(run.IsCompleted);
      Assert.Equal(admission.Entered - 1, admission.Released);
      Assert.Equal("freeze,hide,exit", string.Join(',', host.Events));
      await oldProcess.StopAsync();
      var outcome = await run.WaitAsync(TimeSpan.FromSeconds(10));
      Assert.Equal(UpdateOutcomeKind.Failed, outcome.Kind);
      Assert.Equal(UpdatePhase.Committed, journal.Read().Value!.Phase);
      Assert.Null(journal.Read().Value!.Finalization);
      Assert.True(LaunchResolver.SameTarget(journal.Read().Value!.NewTarget,
        state.Read<CurrentTarget>(pointer).Value!.Current));
      Assert.Equal(admission.Entered, admission.Released);
    }
    finally
    {
      if (candidateProcess is not null)
      {
        if (!candidateProcess.HasExited) candidateProcess.Kill();
        await candidateProcess.WaitForExitAsync();
        candidateProcess.Dispose();
      }
      if (!oldProcess.HasExited) await oldProcess.StopAsync();
      oldProcess.Dispose();
      oldExecutable.Dispose();
      await Task.Delay(250);
    }
  }

  [Fact]
  public async Task RefusedHideRestoresOldHostAfterNativeSelfCheckAndDurablePreparation()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var fixture = new PackageStagerTests.Fixture(runnableSelfCheck: true);
    var oldTarget = PrepareNativeOldTarget(fixture);
    var failCommittedJournalWrite = false;
    var (stager, journal, state, _) = fixture.CreateDurableStager(beginJournal: false,
      writeFault: point =>
      {
        if (failCommittedJournalWrite && point == StateWritePoint.Replaced)
        {
          failCommittedJournalWrite = false;
          throw new IOException("Injected committed journal transition failure.");
        }
      });
    var pointer = Path.Combine(fixture.Layout.Records.Path, "current.json");
    state.Write(pointer, new CurrentTarget(1, fixture.Layout.InstallationId, oldTarget, null));
    var settings = Path.Combine(fixture.Layout.DataRoot, "settings.json");
    File.WriteAllText(settings, "{\"schemaVersion\":1}");
    var current = new CurrentTargetStore(state, journal, fixture.Layout);
    var host = new RefusingOldHost();
    var admission = new TrackingLeaseStore();
    using var coordinatorProcess = SupervisedProcess.CaptureCurrent();
    using var oldExecutable = VerifiedExecutable.PinFile(oldTarget.ExecutablePath);
    using var oldProcess = ProcessSupervisor.Start(oldExecutable, ["hold"],
      new Dictionary<string, string>());
    var coordinator = new UpdateCoordinator(new FixedManifestReader(fixture.Manifest),
      stager, journal, current, admission, fixture.Store, coordinatorProcess, oldProcess);
    var update = new ConfirmedUpdate(fixture.Release,
      ReleaseSelector.Select([fixture.Release]).Identity!, fixture.Manifest.ManifestSha256!,
      oldTarget, Guid.NewGuid());

    var outcome = await coordinator.RunAsync(update, fixture.Layout, host,
      CancellationToken.None);

    Assert.Equal(UpdateOutcomeKind.Failed, outcome.Kind);
    var unresolved = coordinator.UnresolvedFailure.IsCompleted ?
      (await coordinator.UnresolvedFailure).ToString() : "none";
    Assert.True(string.Join(',', host.Events) == "freeze,hide,restore",
      "events=" + string.Join(',', host.Events) + " outcome=" + outcome +
      " journal=" + journal.Read().Kind + " unresolved=" + unresolved);
    Assert.Equal(1, admission.Entered);
    Assert.Equal(1, admission.Released);
    var transaction = journal.Read().Value!;
    Assert.Equal(UpdatePhase.Failed, transaction.Phase);
    Assert.NotNull(transaction.NewTarget);
    Assert.NotNull(transaction.Promotion?.Promoted);
    Assert.Equal(WriterFinalizationState.OldRestoredAfterCandidateExit,
      transaction.Finalization?.State);
    Assert.True(UpdateTransaction.SameMapping(transaction.PriorMapping,
      state.Read<CurrentTarget>(pointer).Value));

    var acceptedHide = new RefusingOldHost(acceptHide: true);
    var retry = await coordinator.RunAsync(update, fixture.Layout, acceptedHide,
      CancellationToken.None);
    Assert.Equal(UpdateOutcomeKind.RolledBack, retry.Kind);
    Assert.Equal("freeze,hide,restore", string.Join(',', acceptedHide.Events));
    Assert.NotEqual(transaction.Id, journal.Read().Value!.Id);
    Assert.Equal(UpdatePhase.Failed, journal.Read().Value!.Phase);
    Assert.Equal(2, admission.Entered);
    Assert.Equal(2, admission.Released);

    var changedSettingsHost = new RefusingOldHost();
    var changedSettings = new UpdateCoordinator(new FixedManifestReader(fixture.Manifest,
      () => File.WriteAllText(settings, "{\"schemaVersion\":1,\"changed\":true}")),
      stager, journal, current, admission, fixture.Store, coordinatorProcess, oldProcess);
    var mutation = await changedSettings.RunAsync(update, fixture.Layout, changedSettingsHost,
      CancellationToken.None);
    Assert.Equal(UpdateOutcomeKind.Failed, mutation.Kind);
    Assert.Equal("freeze,restore", string.Join(',', changedSettingsHost.Events));
    Assert.Equal(UpdatePhase.Failed, journal.Read().Value!.Phase);
    Assert.Equal(3, admission.Released);

    File.WriteAllText(settings, "{\"schemaVersion\":1}");
    using var helper = VerifiedExecutable.PinFile(ProcessSupervisorTests.HelperPath());
    var nativeOld = oldProcess;
    Process? nativeCandidate = null;
    SupervisedProcess LaunchNative(CandidateStartupRequest request, bool ready,
      int delayMilliseconds = 0)
    {
      string[] args = ready ?
      [
        "pipe-ready", request.PipeName, request.TransactionId.ToString("D"),
        coordinatorProcess.Identity.Pid.ToString(CultureInfo.InvariantCulture),
        coordinatorProcess.Identity.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
        coordinatorProcess.Identity.ExecutablePath,
        Convert.ToHexString(Convert.FromBase64String(request.Nonce)),
        coordinatorProcess.ImageIdentity.VolumeSerial.ToString(CultureInfo.InvariantCulture),
        coordinatorProcess.ImageIdentity.FileIdHex, coordinatorProcess.ImageSha256
      ] : ["hold"];
      if (delayMilliseconds > 0)
        args = [.. args, delayMilliseconds.ToString(CultureInfo.InvariantCulture)];
      else if (ready)
        args = [.. args, "activate",
          nativeOld.Identity.Pid.ToString(CultureInfo.InvariantCulture),
          nativeOld.Identity.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
          nativeOld.Identity.ExecutablePath,
          nativeOld.ImageIdentity.VolumeSerial.ToString(CultureInfo.InvariantCulture),
          nativeOld.ImageIdentity.FileIdHex, nativeOld.ImageSha256];
      var child = ProcessSupervisor.Start(helper, args, new Dictionary<string, string>());
      nativeCandidate = Process.GetProcessById(child.Identity.Pid);
      return child;
    }
    try
    {
      var noAckHost = new NativeOldHost(nativeOld);
      var noAckCoordinator = new UpdateCoordinator(new FixedManifestReader(fixture.Manifest),
        stager, journal, current, admission, fixture.Store, coordinatorProcess, nativeOld)
      {
        CandidateLauncher = (_, request, _) => LaunchNative(request, false),
        ReadinessTimeout = TimeSpan.FromMilliseconds(300)
      };
      var noAck = await noAckCoordinator.RunAsync(update, fixture.Layout, noAckHost,
        CancellationToken.None);
      Assert.Equal(UpdateOutcomeKind.RolledBack, noAck.Kind);
      Assert.Equal("freeze,hide,restore", string.Join(',', noAckHost.Events));
      Assert.NotNull(nativeCandidate);
      Assert.True(nativeCandidate.HasExited);
      Assert.True(nativeOld.HasExited == false);
      Assert.Equal(UpdatePhase.Failed, journal.Read().Value!.Phase);
      nativeCandidate.Dispose();
      nativeCandidate = null;

      var wrongHost = new NativeOldHost(nativeOld);
      var wrongCoordinator = new UpdateCoordinator(new FixedManifestReader(fixture.Manifest),
        stager, journal, current, admission, fixture.Store, coordinatorProcess, nativeOld)
      {
        CandidateLauncher = (_, request, _) => LaunchNative(request with
        {
          Nonce = Convert.ToBase64String(UpdatePipe.NewNonce())
        }, true)
      };
      var wrong = await wrongCoordinator.RunAsync(update, fixture.Layout, wrongHost,
        CancellationToken.None);
      Assert.Equal(UpdateOutcomeKind.RolledBack, wrong.Kind);
      Assert.Equal("freeze,hide,restore", string.Join(',', wrongHost.Events));
      Assert.NotNull(nativeCandidate);
      Assert.True(nativeCandidate.HasExited);
      nativeCandidate.Dispose();
      nativeCandidate = null;

      var lateHost = new NativeOldHost(nativeOld);
      var lateCoordinator = new UpdateCoordinator(new FixedManifestReader(fixture.Manifest),
        stager, journal, current, admission, fixture.Store, coordinatorProcess, nativeOld)
      {
        CandidateLauncher = (_, request, _) => LaunchNative(request, true, 1000),
        ReadinessTimeout = TimeSpan.FromMilliseconds(200)
      };
      var late = await lateCoordinator.RunAsync(update, fixture.Layout, lateHost,
        CancellationToken.None);
      Assert.Equal(UpdateOutcomeKind.RolledBack, late.Kind);
      Assert.NotNull(nativeCandidate);
      Assert.True(nativeCandidate.HasExited);
      Assert.Equal("freeze,hide,restore", string.Join(',', lateHost.Events));
      nativeCandidate.Dispose();
      nativeCandidate = null;

      using var cancellation = new CancellationTokenSource();
      var cancelledHost = new NativeOldHost(nativeOld);
      var cancelledCoordinator = new UpdateCoordinator(new FixedManifestReader(fixture.Manifest),
        stager, journal, current, admission, fixture.Store, coordinatorProcess, nativeOld)
      {
        CandidateLauncher = (_, request, _) =>
        {
          var child = LaunchNative(request, false);
          cancellation.Cancel();
          return child;
        }
      };
      var cancelled = await cancelledCoordinator.RunAsync(update, fixture.Layout,
        cancelledHost, cancellation.Token);
      Assert.Equal(UpdateOutcomeKind.Cancelled, cancelled.Kind);
      Assert.NotNull(nativeCandidate);
      Assert.True(nativeCandidate.HasExited);
      nativeCandidate.Dispose();
      nativeCandidate = null;

      using var stopFailureCancellation = new CancellationTokenSource();
      var stopAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      SupervisedProcess? stopFailureChild = null;
      var stopFailureHost = new NativeOldHost(nativeOld);
      var stopFailureCoordinator = new UpdateCoordinator(
        new FixedManifestReader(fixture.Manifest), stager, journal, current,
        admission, fixture.Store, coordinatorProcess, nativeOld)
      {
        CandidateLauncher = (_, request, _) =>
        {
          stopFailureChild = LaunchNative(request, false);
          stopFailureCancellation.Cancel();
          return stopFailureChild;
        },
        CandidateStop = _ =>
        {
          stopAttempted.TrySetResult();
          return Task.FromException(new IOException("Injected native stop failure."));
        }
      };
      var stopFailureRun = stopFailureCoordinator.RunAsync(update, fixture.Layout,
        stopFailureHost, stopFailureCancellation.Token);
      await stopAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
      Assert.IsType<IOException>(await stopFailureCoordinator.UnresolvedFailure.WaitAsync(
        TimeSpan.FromSeconds(10)));
      Assert.False(stopFailureRun.IsCompleted);
      Assert.Equal(admission.Entered - 1, admission.Released);
      Assert.Equal("freeze,hide", string.Join(',', stopFailureHost.Events));
      Assert.NotNull(stopFailureChild);
      Assert.False(stopFailureChild.HasExited);
      await stopFailureChild.StopAsync();
      var stopped = await stopFailureRun;
      Assert.Equal(UpdateOutcomeKind.Cancelled, stopped.Kind);
      Assert.Equal("freeze,hide,restore", string.Join(',', stopFailureHost.Events));
      Assert.NotNull(nativeCandidate);
      Assert.True(nativeCandidate.HasExited);
      nativeCandidate.Dispose();
      nativeCandidate = null;

      var lostHost = new NativeOldHost(nativeOld);
      var lostCoordinator = new UpdateCoordinator(new FixedManifestReader(fixture.Manifest),
        stager, journal, current, admission, fixture.Store, coordinatorProcess, nativeOld)
      {
        CandidateLauncher = (_, request, _) => LaunchNative(request, true),
        AfterReady = child => child.StopAsync().GetAwaiter().GetResult()
      };
      var lost = await lostCoordinator.RunAsync(update, fixture.Layout, lostHost,
        CancellationToken.None);
      Assert.True(lost.Kind == UpdateOutcomeKind.RolledBack,
        "lost=" + lost + " events=" + string.Join(',', lostHost.Events) +
        " journal=" + journal.Read().Value?.Phase + " unresolved=" +
        (lostCoordinator.UnresolvedFailure.IsCompleted ?
          (await lostCoordinator.UnresolvedFailure).ToString() : "none"));
      Assert.Equal("freeze,hide,restore", string.Join(',', lostHost.Events));
      Assert.NotNull(nativeCandidate);
      Assert.True(nativeCandidate.HasExited);
      Assert.Equal(UpdatePhase.Failed, journal.Read().Value!.Phase);
      nativeCandidate.Dispose();
      nativeCandidate = null;

      var journalFailureHost = new NativeOldHost(nativeOld);
      var journalFailureCoordinator = new UpdateCoordinator(
        new FixedManifestReader(fixture.Manifest), stager, journal, current,
        admission, fixture.Store, coordinatorProcess, nativeOld)
      {
        CandidateLauncher = (_, request, _) => LaunchNative(request, true),
        AfterPointerCommit = () => failCommittedJournalWrite = true
      };
      var journalFailure = await journalFailureCoordinator.RunAsync(update,
        fixture.Layout, journalFailureHost, CancellationToken.None);
      Assert.Equal(UpdateOutcomeKind.RolledBack, journalFailure.Kind);
      Assert.Equal("freeze,hide,restore", string.Join(',', journalFailureHost.Events));
      Assert.NotNull(nativeCandidate);
      Assert.True(nativeCandidate.HasExited);
      Assert.True(UpdateTransaction.SameMapping(journal.Read().Value!.PriorMapping,
        state.Read<CurrentTarget>(pointer).Value));
      Assert.Equal(UpdatePhase.Failed, journal.Read().Value!.Phase);
      nativeCandidate.Dispose();
      nativeCandidate = null;

      var nativeHost = new NativeOldHost(nativeOld);
      var nativeCoordinator = new UpdateCoordinator(new FixedManifestReader(fixture.Manifest),
        stager, journal, current, admission, fixture.Store, coordinatorProcess, nativeOld)
      {
        CandidateLauncher = (_, request, _) => LaunchNative(request, true)
      };
      var installed = await nativeCoordinator.RunAsync(update, fixture.Layout, nativeHost,
        CancellationToken.None);
      Assert.Equal(UpdateOutcomeKind.Installed, installed.Kind);
      Assert.Equal("freeze,hide,exit", string.Join(',', nativeHost.Events));
      Assert.True(nativeOld.HasExited);
      Assert.NotNull(nativeCandidate);
      Assert.False(nativeCandidate.HasExited);
      Assert.Equal(UpdatePhase.Committed, journal.Read().Value!.Phase);
      Assert.Equal(WriterFinalizationState.CandidateActiveAfterOldExit,
        journal.Read().Value!.Finalization?.State);
      Assert.True(LaunchResolver.SameTarget(journal.Read().Value!.NewTarget,
        state.Read<CurrentTarget>(pointer).Value!.Current));
    }
    finally
    {
      if (nativeCandidate is not null)
      {
        if (!nativeCandidate.HasExited) nativeCandidate.Kill();
        await nativeCandidate.WaitForExitAsync();
        nativeCandidate.Dispose();
      }
      if (!nativeOld.HasExited) await nativeOld.StopAsync();
      nativeOld.Dispose();
      oldExecutable.Dispose();
      await Task.Delay(250);
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task PostCommitRequiresActivationAndTrustedJournal(bool corruptJournal)
  {
    if (!OperatingSystem.IsWindows()) return;
    using var fixture = new PackageStagerTests.Fixture(runnableSelfCheck: true);
    var oldTarget = PrepareNativeOldTarget(fixture);
    var (stager, journal, state, _) = fixture.CreateDurableStager(beginJournal: false);
    var pointer = Path.Combine(fixture.Layout.Records.Path, "current.json");
    state.Write(pointer, new CurrentTarget(1, fixture.Layout.InstallationId, oldTarget, null));
    File.WriteAllText(Path.Combine(fixture.Layout.DataRoot, "settings.json"),
      "{\"schemaVersion\":1}");
    var current = new CurrentTargetStore(state, journal, fixture.Layout);
    var admission = new TrackingLeaseStore();
    using var coordinatorProcess = SupervisedProcess.CaptureCurrent();
    using var oldExecutable = VerifiedExecutable.PinFile(oldTarget.ExecutablePath);
    using var oldProcess = ProcessSupervisor.Start(oldExecutable, ["hold"],
      new Dictionary<string, string>());
    using var helper = VerifiedExecutable.PinFile(ProcessSupervisorTests.HelperPath());
    SupervisedProcess? child = null;
    var host = new NativeOldHost(oldProcess);
    var coordinator = new UpdateCoordinator(new FixedManifestReader(fixture.Manifest),
      stager, journal, current, admission, fixture.Store, coordinatorProcess, oldProcess)
    {
      ReadinessTimeout = TimeSpan.FromSeconds(2),
      CandidateLauncher = (_, request, _) =>
      {
        string[] args =
        [
          "pipe-ready", request.PipeName, request.TransactionId.ToString("D"),
          coordinatorProcess.Identity.Pid.ToString(CultureInfo.InvariantCulture),
          coordinatorProcess.Identity.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
          coordinatorProcess.Identity.ExecutablePath,
          Convert.ToHexString(Convert.FromBase64String(request.Nonce)),
          coordinatorProcess.ImageIdentity.VolumeSerial.ToString(CultureInfo.InvariantCulture),
          coordinatorProcess.ImageIdentity.FileIdHex, coordinatorProcess.ImageSha256,
          "ignore", oldProcess.Identity.Pid.ToString(CultureInfo.InvariantCulture),
          oldProcess.Identity.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
          oldProcess.Identity.ExecutablePath,
          oldProcess.ImageIdentity.VolumeSerial.ToString(CultureInfo.InvariantCulture),
          oldProcess.ImageIdentity.FileIdHex, oldProcess.ImageSha256
        ];
        child = ProcessSupervisor.Start(helper, args, new Dictionary<string, string>());
        return child;
      },
      AfterPointerCommit = corruptJournal ? () =>
      {
        File.WriteAllText(Path.Combine(fixture.Layout.Records.Path, "journal.json"), "invalid");
      }
      : null
    };
    var update = new ConfirmedUpdate(fixture.Release,
      ReleaseSelector.Select([fixture.Release]).Identity!, fixture.Manifest.ManifestSha256!,
      oldTarget, Guid.NewGuid());
    try
    {
      var run = coordinator.RunAsync(update, fixture.Layout, host, CancellationToken.None);
      if (corruptJournal)
      {
        var failure = await coordinator.UnresolvedFailure.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<InvalidDataException>(failure);
        Assert.False(run.IsCompleted);
        Assert.False(oldProcess.HasExited);
        Assert.DoesNotContain("restore", host.Events);
        Assert.Equal(admission.Entered - 1, admission.Released);
        await oldProcess.StopAsync();
      }
      var outcome = await run.WaitAsync(TimeSpan.FromSeconds(10));
      Assert.Equal(UpdateOutcomeKind.Failed, outcome.Kind);
      Assert.Null(journal.Read().Value?.Finalization);
      Assert.False(LaunchResolver.SameTarget(oldTarget,
        state.Read<CurrentTarget>(pointer).Value!.Current));
      Assert.Equal(admission.Entered, admission.Released);
      Assert.NotNull(child);
    }
    finally
    {
      if (!oldProcess.HasExited) await oldProcess.StopAsync();
      oldProcess.Dispose();
      oldExecutable.Dispose();
      await Task.Delay(250);
    }
  }

  private sealed class FixedManifestReader(VerifiedManifest manifest,
    Action? beforeReturn = null) : ISignedReleaseReader
  {
    public Task<VerifiedManifest> ReadAsync(ReleaseCandidate release, int settingsSchema,
      CancellationToken cancellationToken)
    {
      beforeReturn?.Invoke();
      return Task.FromResult(manifest);
    }
  }

  private sealed class RefusingOldHost(bool acceptHide = false) : IOldApplicationHost
  {
    public List<string> Events { get; } = [];
    public Task<bool> FreezeAsync(CancellationToken cancellationToken = default)
    {
      Events.Add("freeze");
      return Task.FromResult(true);
    }
    public Task<bool> HideAndReleaseGuiAsync(CancellationToken cancellationToken = default)
    {
      Events.Add("hide");
      return Task.FromResult(acceptHide);
    }
    public Task<bool> RestoreAndAcquireGuiAsync(CancellationToken cancellationToken = default)
    {
      Events.Add("restore");
      return Task.FromResult(true);
    }
    public Task ExitAsync(CancellationToken cancellationToken = default) =>
      throw new InvalidOperationException("Exit must not be requested after hide refusal.");
  }

  private sealed class ThrowingFreezeHost(CancellationTokenSource? cancellation = null)
    : IOldApplicationHost
  {
    public List<string> Events { get; } = [];
    public Task<bool> FreezeAsync(CancellationToken cancellationToken = default)
    {
      Events.Add("freeze");
      if (cancellation is not null)
      {
        cancellation.Cancel();
        throw new OperationCanceledException(cancellation.Token);
      }
      throw new IOException("The remote host froze before its reply was lost.");
    }
    public Task<bool> HideAndReleaseGuiAsync(CancellationToken cancellationToken = default) =>
      throw new InvalidOperationException();
    public Task<bool> RestoreAndAcquireGuiAsync(CancellationToken cancellationToken = default)
    {
      Events.Add("restore");
      return Task.FromResult(true);
    }
    public Task ExitAsync(CancellationToken cancellationToken = default) =>
      throw new InvalidOperationException();
  }

  private sealed class TrackingLeaseStore : IActivityLeaseStore
  {
    public int Entered { get; private set; }
    public int Released { get; private set; }
    public IDisposable? TryEnterJob() => null;
    public IDisposable TryEnterUpdate()
    {
      Entered++;
      return new Release(() => Released++);
    }
    private sealed class Release(Action release) : IDisposable
    {
      public void Dispose() => release();
    }
  }

  private sealed class RefusedLeaseStore : IActivityLeaseStore
  {
    public IDisposable? TryEnterJob() => null;
    public IDisposable? TryEnterUpdate() => null;
  }

  private sealed class NativeOldHost(SupervisedProcess process, bool stopOnExit = true)
    : IOldApplicationHost
  {
    public List<string> Events { get; } = [];
    public TaskCompletionSource ExitRequested { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> FreezeAsync(CancellationToken cancellationToken = default)
    {
      Events.Add("freeze");
      return Task.FromResult(true);
    }
    public Task<bool> HideAndReleaseGuiAsync(CancellationToken cancellationToken = default)
    {
      Events.Add("hide");
      return Task.FromResult(true);
    }
    public Task<bool> RestoreAndAcquireGuiAsync(CancellationToken cancellationToken = default)
    {
      Events.Add("restore");
      return Task.FromResult(true);
    }
    public async Task ExitAsync(CancellationToken cancellationToken = default)
    {
      Events.Add("exit");
      ExitRequested.TrySetResult();
      if (stopOnExit) await process.StopAsync(cancellationToken);
    }
  }

  private static ReleaseCandidate Candidate(string tag)
  {
    var version = tag[1..];
    var prefix = $"BookSplice-{version}-win-x64";
    var names = new[] { prefix + ".zip", prefix + ".zip.sha256",
      prefix + ".update.json", prefix + ".update.sig.json" };
    var assets = names.Select((name, index) => new ReleaseAsset(index + 1, name,
      new Uri($"https://github.com/nickwolf/booksplice/releases/download/{tag}/{name}"), 100))
      .ToArray();
    return new ReleaseCandidate(7, tag, SemanticVersion.ParseTag(tag),
      new Uri($"https://github.com/nickwolf/booksplice/releases/tag/{tag}"),
      "notes", false, false, assets);
  }

  private static OwnedOrOriginalTarget PrepareNativeOldTarget(PackageStagerTests.Fixture fixture)
  {
    var root = fixture.Layout.OriginalApplicationPath;
    OriginalPackageFixture.Write(root, "0.1.0");
    var helper = ProcessSupervisorTests.HelperPath();
    var sourceBase = Path.Combine(Path.GetDirectoryName(helper)!, "BookSplice.LeaseHost");
    File.Copy(helper, Path.Combine(root, "BookSplice.Gui.exe"), true);
    foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
      File.Copy(sourceBase + extension, Path.Combine(root, "BookSplice.LeaseHost" + extension), true);
    var updates = Path.Combine(Path.GetDirectoryName(helper)!, "BookSplice.Updates.dll");
    File.Copy(updates, Path.Combine(root, "BookSplice.Updates.dll"), true);
    var installation = InstallationRecord.FromLayout(fixture.Layout, "0.1.0");
    return new OwnedOrOriginalTarget(TargetKind.Original,
      installation.OriginalExecutablePath, installation.OriginalIdentity,
      installation.OriginalExecutableIdentity, installation.OriginalPackage,
      installation.OriginalVersion, installation.OriginalProtocolVersion, null);
  }
}
