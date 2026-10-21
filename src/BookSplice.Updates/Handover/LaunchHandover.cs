using System.Runtime.Versioning;
using System.Security.Cryptography;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Handover;

[SupportedOSPlatform("windows")]
public sealed class LaunchHandover(
  AtomicStateStore state, UpdateJournal journal, CurrentTargetStore targets,
  IActivityLeaseStore activity, OwnedDirectoryStore directories, SupervisedProcess self)
{
  internal Func<VerifiedExecutable, CandidateStartupRequest, SupervisedProcess>? CandidateLauncher
  { get; init; }
  internal Func<SupervisedProcess, Task>? CandidateStop { get; init; }
  internal Func<InstallationLayout, LaunchDecision>? LaunchMapped { get; init; }
  internal Action? AfterReady { get; init; }
  internal TimeSpan OldExitTimeout { get; init; } = TimeSpan.FromSeconds(60);
  internal TimeSpan AdmissionTimeout { get; init; } = TimeSpan.FromSeconds(10);
  internal TimeSpan ReadinessTimeout { get; init; } = UpdateLimits.ReadinessTimeout;
  internal RecoveryProcessProbe Probe { get; init; } = new();

  public async Task<UpdateOutcome> RunAsync(Guid transactionId, int oldGuiPid,
    long oldGuiStartTicks, InstallationLayout layout, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(layout);
    var snapshot = JournalSnapshot.Read(state, layout.Records.Path);
    if (snapshot.Transaction.Value is not { Phase: UpdatePhase.Prepared } prepared ||
        prepared.Id != transactionId || snapshot.Installation.Value is not { } installation)
      return new UpdateOutcome(UpdateOutcomeKind.Failed,
        "The update is no longer waiting to start.");

    if (!await WaitForOldExitAsync(prepared.OldTarget, installation,
        new ProcessIdentity(oldGuiPid, oldGuiStartTicks, prepared.OldTarget.ExecutablePath),
        cancellationToken).ConfigureAwait(false))
    {
      TryFail(transactionId, UpdatePhase.Prepared);
      return new UpdateOutcome(UpdateOutcomeKind.Failed,
        "The previous BookSplice window did not close, so the update was not started.");
    }

    using var admission = await EnterUpdateAsync(cancellationToken).ConfigureAwait(false);
    if (admission is null)
    {
      var launched = TryFail(transactionId, UpdatePhase.Prepared) && Launch(layout);
      return new UpdateOutcome(UpdateOutcomeKind.Failed,
        "A BookSplice job or another update is running, so the update was not started.",
        launched);
    }
    return await HandOverAsync(transactionId, layout, cancellationToken).ConfigureAwait(false);
  }

  private async Task<UpdateOutcome> HandOverAsync(Guid transactionId,
    InstallationLayout layout, CancellationToken cancellationToken)
  {
    var snapshot = JournalSnapshot.Read(state, layout.Records.Path);
    if (snapshot.Transaction.Value is not { Phase: UpdatePhase.Prepared, NewTarget: { } } transaction ||
        transaction.Id != transactionId)
      return new UpdateOutcome(UpdateOutcomeKind.Failed,
        "The update was already finished by another BookSplice launcher.");

    var phase = UpdatePhase.Prepared;
    SupervisedProcess? candidate = null;
    UnverifiedChild? unverifiedChild = null;
    VerifiedExecutable? executable = null;
    UpdateSession? session = null;
    var nonce = Convert.FromHexString(transaction.Nonce);
    try
    {
      layout.Validate(directories);
      if (snapshot.Installation.Value is not { } installation ||
          snapshot.Ownership.Value is not { } ownership ||
          !RecoveryPolicy.TargetBelongs(transaction.NewTarget, installation, ownership) ||
          !UpdateTransaction.SameMapping(snapshot.Current.Value, transaction.PriorMapping))
        throw new InvalidDataException("The prepared update no longer matches this installation.");
      executable = LaunchResolver.OpenTarget(layout, installation, transaction.NewTarget);
      await VerifySettingsAsync(transaction, layout, cancellationToken).ConfigureAwait(false);

      var pipeName = UpdatePipe.NewPipeName();
      using var server = UpdatePipe.CreateServer(pipeName);
      var request = new CandidateStartupRequest(pipeName, transactionId,
        Convert.ToBase64String(nonce), self.Identity, self.ImageIdentity, self.ImageSha256,
        layout.DataRoot, layout.DataIdentity, transaction.SettingsSha256 ?? "absent",
        transaction.NewTarget.Version);
      journal.Transition(transactionId, UpdatePhase.Prepared, UpdatePhase.Starting);
      phase = UpdatePhase.Starting;
      candidate = CandidateLauncher is { } launch ? launch(executable, request) :
        ProcessSupervisor.Start(executable, ["--update-candidate", request.Encode()],
          LaunchResolver.ChildEnvironment(layout.DataRoot, layout.DataIdentity));
      var started = candidate;
      journal.Change(transactionId, UpdatePhase.Starting, current => current with
      {
        NewProcess = started.Identity
      });

      using var readyDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      readyDeadline.CancelAfter(ReadinessTimeout);
      var exited = candidate.WaitForExitAsync(readyDeadline.Token);
      session = await WhileRunningAsync(UpdatePipe.AcceptAsync(server, candidate, transactionId,
        nonce, self, readyDeadline.Token), exited).ConfigureAwait(false);
      var ready = await WhileRunningAsync(session.ReceiveAsync(readyDeadline.Token), exited)
        .ConfigureAwait(false);
      if (ready.Kind != UpdateMessageKind.Ready)
        throw new IOException("The new version did not report readiness.");
      readyDeadline.Cancel();

      AfterReady?.Invoke();
      await VerifySettingsAsync(transaction, layout, CancellationToken.None).ConfigureAwait(false);
      layout.Validate(directories);
      if (candidate.HasExited)
        throw new IOException("The new version exited before the update was committed.");
      targets.Commit(transactionId, executable);
      journal.Transition(transactionId, UpdatePhase.Starting, UpdatePhase.Committed);
      phase = UpdatePhase.Committed;
      await session.SendAsync(UpdateMessageKind.Commit, CancellationToken.None)
        .ConfigureAwait(false);
      return new UpdateOutcome(UpdateOutcomeKind.Committed);
    }
    catch (Exception error)
    {
      if (error is UnverifiedChildException failedStart) unverifiedChild = failedStart.Child;
      var stopped = await StopAsync(candidate, unverifiedChild).ConfigureAwait(false);
      if (phase != UpdatePhase.Committed) TryFail(transactionId, phase);
      if (!stopped)
        return new UpdateOutcome(UpdateOutcomeKind.Failed, error.Message +
          " The new version could not be stopped, so the previous version was not started.");
      executable?.Dispose();
      return new UpdateOutcome(UpdateOutcomeKind.Failed, error.Message, Launch(layout));
    }
    finally
    {
      session?.Dispose();
      candidate?.Dispose();
      unverifiedChild?.Dispose();
      executable?.Dispose();
      CryptographicOperations.ZeroMemory(nonce);
    }
  }

  private static async Task<T> WhileRunningAsync<T>(Task<T> operation, Task exited)
  {
    if (await Task.WhenAny(operation, exited).ConfigureAwait(false) == exited &&
        exited.IsCompletedSuccessfully)
    {
      _ = operation.ContinueWith(task => _ = task.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
      throw new IOException("The new version exited before it reported readiness.");
    }
    return await operation.ConfigureAwait(false);
  }

  private async Task<bool> WaitForOldExitAsync(OwnedOrOriginalTarget oldTarget,
    InstallationRecord installation, ProcessIdentity oldGui, CancellationToken cancellationToken)
  {
    var sha256 = oldTarget.Package?.Files.SingleOrDefault(file =>
        file.RelativePath == "BookSplice.Gui.exe")?.Sha256 ??
      (oldTarget.Kind == TargetKind.Original ? installation.OriginalExecutableSha256 : null);
    if (sha256 is null || oldGui.Pid <= 0 || oldGui.StartTimeUtcTicks <= 0) return false;
    using var observation = Probe.Observe(oldGui, oldTarget.ExecutableIdentity, sha256);
    if (observation.Life == ProcessLife.Exited) return true;
    if (observation.Process is not { } process) return false;
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(OldExitTimeout);
    try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
    catch (OperationCanceledException) { }
    return process.HasExited;
  }

  private async Task<IDisposable?> EnterUpdateAsync(CancellationToken cancellationToken)
  {
    var deadline = DateTime.UtcNow + AdmissionTimeout;
    while (true)
    {
      if (activity.TryEnterUpdate() is { } admission) return admission;
      if (DateTime.UtcNow >= deadline) return null;
      await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
    }
  }

  private async Task<bool> StopAsync(SupervisedProcess? candidate, UnverifiedChild? child)
  {
    try
    {
      if (child is not null && !child.HasExited)
      {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await child.StopAndWaitAsync(deadline.Token).ConfigureAwait(false);
      }
      if (candidate is not null && !candidate.HasExited)
      {
        if (CandidateStop is { } stop) await stop(candidate).ConfigureAwait(false);
        else
        {
          using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
          await candidate.StopAsync(deadline.Token).ConfigureAwait(false);
        }
      }
    }
    catch (Exception error) when (error is IOException or InvalidDataException or
      OperationCanceledException or System.ComponentModel.Win32Exception or
      InvalidOperationException)
    { }
    return (child is null || child.HasExited) && (candidate is null || candidate.HasExited);
  }

  private bool TryFail(Guid transactionId, UpdatePhase phase)
  {
    try
    {
      journal.Transition(transactionId, phase, UpdatePhase.Failed);
      return true;
    }
    catch (Exception error) when (error is IOException or InvalidDataException or
      UnauthorizedAccessException)
    { return false; }
  }

  private bool Launch(InstallationLayout layout)
  {
    try
    {
      var decision = LaunchMapped is { } launch ? launch(layout) :
        LaunchResolver.LaunchCurrent(layout, state);
      return decision.Kind == LaunchDecisionKind.Ready;
    }
    catch (Exception error) when (error is IOException or InvalidDataException or
      UnauthorizedAccessException or ArgumentException or InvalidOperationException or
      System.ComponentModel.Win32Exception)
    { return false; }
  }

  private static async Task VerifySettingsAsync(UpdateTransaction transaction,
    InstallationLayout layout, CancellationToken cancellationToken)
  {
    var settings = await SettingsSnapshot.CaptureAsync(
      Path.Combine(layout.DataRoot, "settings.json"), cancellationToken).ConfigureAwait(false);
    if (settings.IsAbsent != transaction.SettingsAbsent ||
        !string.Equals(settings.Sha256, transaction.SettingsSha256, StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("Settings changed after the update was prepared.");
  }
}
