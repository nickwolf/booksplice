using System.Runtime.Versioning;
using System.Security.Cryptography;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Transport;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Handover;

public sealed record ConfirmedUpdate(ReleaseCandidate Release, ReleaseIdentity ReleaseIdentity,
  string ManifestSha256, OwnedOrOriginalTarget OldTarget, Guid UserConfirmationId);

public enum UpdateOutcomeKind { Installed, Cancelled, Deferred, RolledBack, Failed }

public sealed record UpdateOutcome(UpdateOutcomeKind Kind, string? Reason = null);

public sealed class UpdateDeferredException(string message) : Exception(message);

[SupportedOSPlatform("windows")]
public sealed class UpdateCoordinator(
  ISignedReleaseReader releases, PackageStager stager, UpdateJournal journal,
  CurrentTargetStore targets, IActivityLeaseStore activity, OwnedDirectoryStore directories,
  SupervisedProcess coordinator, SupervisedProcess oldProcess)
{
  private readonly TaskCompletionSource<Exception> _unresolvedFailure =
    new(TaskCreationOptions.RunContinuationsAsynchronously);
  public Task<Exception> UnresolvedFailure => _unresolvedFailure.Task;
  internal Func<VerifiedExecutable, CandidateStartupRequest, InstallationLayout,
    SupervisedProcess>? CandidateLauncher
  { get; init; }
  internal TimeSpan? ReadinessTimeout { get; init; }
  internal TimeSpan? OldExitTimeout { get; init; }
  internal Action<SupervisedProcess>? AfterReady { get; init; }
  internal Func<SupervisedProcess, Task>? CandidateStop { get; init; }
  internal Action? AfterPointerCommit { get; init; }

  public async Task<UpdateOutcome> RunAsync(ConfirmedUpdate update, InstallationLayout layout,
    IOldApplicationHost oldHost, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(layout);
    ArgumentNullException.ThrowIfNull(oldHost);
    if (CheckConfirmation(update) is { } refusal) return refusal;

    using var admission = activity.TryEnterUpdate();
    if (admission is null)
      return new UpdateOutcome(UpdateOutcomeKind.Deferred, "An application job or another update is active.");
    if (!MatchesOldProcess(update.OldTarget, oldProcess))
      return new UpdateOutcome(UpdateOutcomeKind.Deferred,
        "The retained old process differs from the confirmed launch target.");

    var frozen = false;
    var hidden = false;
    var oldRestored = false;
    var begun = false;
    var oldExitMayHaveBeenRequested = false;
    var operation = Guid.NewGuid();
    var settingsPath = Path.Combine(layout.DataRoot, "settings.json");
    SupervisedProcess? candidate = null;
    UnverifiedChild? unverifiedChild = null;
    VerifiedExecutable? executable = null;
    UpdateSession? session = null;
    PromotedPackage? promoted = null;
    StagedPackage? staged = null;
    byte[]? nonce = null;
    try
    {
      frozen = true;
      if (!await oldHost.FreezeAsync(cancellationToken).ConfigureAwait(false))
      {
        frozen = false;
        return new UpdateOutcome(UpdateOutcomeKind.Deferred,
          "The application cannot pause its current work.");
      }
      layout.Validate(directories);
      var prior = targets.CapturePriorMapping(update.OldTarget);
      var settings = await SettingsSnapshot.CaptureAsync(settingsPath, cancellationToken)
        .ConfigureAwait(false);
      var manifest = await releases.ReadAsync(update.Release, settings.SchemaVersion ?? 1,
        cancellationToken).ConfigureAwait(false);
      if (!string.Equals(manifest.ManifestSha256, update.ManifestSha256,
          StringComparison.OrdinalIgnoreCase))
        throw new UpdateDeferredException("The signed manifest changed after confirmation.");
      nonce = UpdatePipe.NewNonce();
      var transaction = new UpdateTransaction(1, layout.InstallationId, operation,
        UpdatePhase.Preparing, update.OldTarget, null, settings.Sha256, settings.IsAbsent,
        UpdateProtocol.ProtocolVersion, Convert.ToHexStringLower(nonce), [],
        coordinator.Identity, oldProcess.Identity, null, null, null, prior);
      var priorJournal = journal.Read();
      if (priorJournal.Kind == StateReadKind.Missing)
        journal.Begin(transaction);
      else if (priorJournal.Kind == StateReadKind.Valid && priorJournal.Value is { } previous &&
               previous.Finalization is not null)
        journal.Begin(transaction, previous.Id);
      else
        throw new UpdateDeferredException("A prior handover still requires recovery.");
      begun = true;
      staged = await stager.StageAsync(update.Release, manifest, layout, operation,
        cancellationToken).ConfigureAwait(false);
      journal.Change(operation, UpdatePhase.Preparing, current => current with
      {
        OwnedPaths = [staged.Stage, staged.Payload]
      });
      await SelfCheckRunner.RunAsync(staged, settings, cancellationToken).ConfigureAwait(false);
      promoted = await stager.PromoteAsync(staged, layout, cancellationToken).ConfigureAwait(false);
      var snapshot = promoted.Inventory.Snapshot();
      var gui = snapshot.Files.Single(file => file.RelativePath == "BookSplice.Gui.exe");
      var newTarget = new OwnedOrOriginalTarget(TargetKind.Managed, promoted.GuiPath,
        promoted.Owned.Identity, gui.Identity, PersistedPackage.FromSnapshot(snapshot),
        manifest.Version, UpdateProtocol.ProtocolVersion, promoted.Owned);
      journal.Change(operation, UpdatePhase.Preparing, current => current with
      {
        NewTarget = newTarget,
        OwnedPaths = [staged.Stage, staged.Payload, promoted.Owned]
      });
      await settings.VerifyUnchangedAsync(settingsPath, cancellationToken).ConfigureAwait(false);
      journal.Transition(operation, UpdatePhase.Preparing, UpdatePhase.Prepared);
      if (!await oldHost.HideAndReleaseGuiAsync(cancellationToken).ConfigureAwait(false))
        throw new IOException("The old GUI refused the handover.");
      hidden = true;

      var pipeName = UpdatePipe.NewPipeName();
      using var server = UpdatePipe.CreateServer(pipeName);
      executable = VerifiedExecutable.FromManaged(promoted.Inventory, newTarget);
      var request = new CandidateStartupRequest(pipeName, operation,
        Convert.ToBase64String(nonce), coordinator.Identity, coordinator.ImageIdentity,
        coordinator.ImageSha256, oldProcess.Identity, oldProcess.ImageIdentity,
        oldProcess.ImageSha256, layout.DataRoot, layout.DataIdentity,
        settings.Sha256 ?? "absent", manifest.Version);
      journal.Transition(operation, UpdatePhase.Prepared, UpdatePhase.Starting);
      candidate = CandidateLauncher is { } launch
        ? launch(executable, request, layout)
        : ProcessSupervisor.Start(executable,
          ["--update-candidate", request.Encode()],
          LaunchResolver.ChildEnvironment(layout.DataRoot, layout.DataIdentity));
      journal.Change(operation, UpdatePhase.Starting, current => current with
      {
        NewProcess = candidate.Identity
      });
      using var handoverDeadline = new CancellationTokenSource(
        ReadinessTimeout ?? UpdateLimits.ReadinessTimeout);
      using var readyDeadline = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken, handoverDeadline.Token);
      session = await UpdatePipe.AcceptAsync(server, candidate, operation, nonce,
        coordinator, readyDeadline.Token).ConfigureAwait(false);
      var ready = await session.ReceiveAsync(readyDeadline.Token).ConfigureAwait(false);
      if (ready.Kind != UpdateMessageKind.Ready || candidate.HasExited)
        throw new IOException("The candidate did not reach authenticated WPF readiness.");
      journal.Transition(operation, UpdatePhase.Starting, UpdatePhase.Ready);
      AfterReady?.Invoke(candidate);
      await settings.VerifyUnchangedAsync(settingsPath, CancellationToken.None)
        .ConfigureAwait(false);
      layout.Validate(directories);
      promoted.Inventory.Revalidate();
      if (candidate.HasExited || oldProcess.HasExited)
        throw new IOException("A supervised process exited before commit.");
      journal.Transition(operation, UpdatePhase.Ready, UpdatePhase.Committing);
      targets.Commit(operation, promoted.Inventory);
      AfterPointerCommit?.Invoke();
      journal.Transition(operation, UpdatePhase.Committing, UpdatePhase.Committed);
      await session.SendAsync(UpdateMessageKind.Commit, CancellationToken.None).ConfigureAwait(false);
      oldExitMayHaveBeenRequested = true;
      await oldHost.ExitAsync(CancellationToken.None).ConfigureAwait(false);
      using var exitDeadline = new CancellationTokenSource(OldExitTimeout ??
        TimeSpan.FromSeconds(40));
      try { await oldProcess.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false); }
      catch (OperationCanceledException)
      {
        _unresolvedFailure.TrySetResult(new TimeoutException(
          "The old GUI exit has not been observed after the committed handover."));
        var first = await Task.WhenAny(oldProcess.WaitForExitAsync(CancellationToken.None),
          candidate.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
        await first.ConfigureAwait(false);
        if (!oldProcess.HasExited)
          throw new IOException("The candidate exited before the old GUI exit was observed.");
      }
      await session.SendAsync(UpdateMessageKind.OldExited, CancellationToken.None).ConfigureAwait(false);
      using var activationDeadline = new CancellationTokenSource(
        ReadinessTimeout ?? UpdateLimits.ReadinessTimeout);
      var activated = await session.ReceiveAsync(activationDeadline.Token).ConfigureAwait(false);
      if (activated.Kind != UpdateMessageKind.Activated || candidate.HasExited ||
          !oldProcess.HasExited)
        throw new IOException("The candidate did not confirm activation after old exit.");
      journal.RecordObservedFinalization(operation, UpdatePhase.Committed,
        WriterFinalizationState.CandidateActiveAfterOldExit, candidate.Identity);
      frozen = false;
      return new UpdateOutcome(UpdateOutcomeKind.Installed);
    }
    catch (Exception error)
    {
      if (error is UnverifiedChildException failedStart)
      {
        unverifiedChild = failedStart.Child;
        await StopAndObserveExitAsync(unverifiedChild).ConfigureAwait(false);
      }
      if (candidate is not null)
        await StopAndObserveExitAsync(candidate).ConfigureAwait(false);
      try
      {
        if (begun)
        {
          var read = journal.Read();
          if (read.Kind != StateReadKind.Valid || read.Value is not { } active ||
              active.Id != operation || active.InstallationId != layout.InstallationId)
            throw new InvalidDataException("The durable handover state cannot be trusted.");
          var phase = active.Phase;
          if (phase == UpdatePhase.Committed &&
              (oldExitMayHaveBeenRequested || candidate is null || !candidate.HasExited))
          {
            if (!oldProcess.HasExited)
              _unresolvedFailure.TrySetResult(new IOException(
                "The committed old GUI exit request remains unresolved."));
            await ObserveExitAsync(oldProcess).ConfigureAwait(false);
            frozen = false;
            return new UpdateOutcome(UpdateOutcomeKind.Failed,
              "The old exit request has an unresolved committed handover.");
          }
          if (phase is UpdatePhase.Preparing or UpdatePhase.Prepared or UpdatePhase.Starting or
              UpdatePhase.Ready or UpdatePhase.Committing or UpdatePhase.Committed)
          {
            if (phase == UpdatePhase.Committed)
              journal.TransitionCommittedCandidateLoss(operation, candidate!.Identity);
            else
              journal.Transition(operation, phase, UpdatePhase.RollingBack);
            if (phase is UpdatePhase.Committing or UpdatePhase.Committed)
            {
              if (candidate is null)
                throw new InvalidDataException("The candidate exit cannot be proved.");
              targets.RestoreAfterCandidateExit(operation, candidate);
            }
            journal.Transition(operation, UpdatePhase.RollingBack, UpdatePhase.Failed);
          }
          else
            throw new InvalidDataException("The active handover phase cannot be unwound.");
        }
      }
      catch (Exception unresolved)
      {
        _unresolvedFailure.TrySetResult(unresolved);
        await ObserveExitAsync(oldProcess).ConfigureAwait(false);
        frozen = false;
        return new UpdateOutcome(UpdateOutcomeKind.Failed,
          "The handover record requires recovery after native writer exit.");
      }
      try
      {
        if (frozen && !oldProcess.HasExited &&
            await oldHost.RestoreAndAcquireGuiAsync(CancellationToken.None).ConfigureAwait(false))
        {
          frozen = false;
          if (begun)
            journal.RecordObservedFinalization(operation, UpdatePhase.Failed,
              WriterFinalizationState.OldRestoredAfterCandidateExit, oldProcess.Identity);
          oldRestored = true;
        }
      }
      catch { }
      if (frozen)
      {
        _unresolvedFailure.TrySetResult(new IOException(
          "The frozen old process could not be restored."));
        await ObserveExitAsync(oldProcess).ConfigureAwait(false);
      }
      frozen = false;
      return new UpdateOutcome(!oldRestored ? UpdateOutcomeKind.Failed :
        error is UpdateDeferredException ? UpdateOutcomeKind.Deferred :
        error is OperationCanceledException && cancellationToken.IsCancellationRequested
          ? UpdateOutcomeKind.Cancelled :
        hidden ? UpdateOutcomeKind.RolledBack : UpdateOutcomeKind.Failed, error.Message);
    }
    finally
    {
      session?.Dispose();
      candidate?.Dispose();
      unverifiedChild?.Dispose();
      executable?.Dispose();
      promoted?.Dispose();
      staged?.Dispose();
      if (nonce is not null) CryptographicOperations.ZeroMemory(nonce);
    }
  }

  private static bool MatchesOldProcess(OwnedOrOriginalTarget target,
    SupervisedProcess process)
  {
    try
    {
      if (!target.IsValid() || target.Package is null ||
          !string.Equals(process.Identity.ExecutablePath, target.ExecutablePath,
            StringComparison.OrdinalIgnoreCase) ||
          process.ImageIdentity != target.ExecutableIdentity)
        return false;
      var executable = target.Package.Files.SingleOrDefault(file =>
        string.Equals(file.RelativePath, "BookSplice.Gui.exe", StringComparison.Ordinal));
      if (executable is null || executable.Identity != process.ImageIdentity ||
          !string.Equals(executable.Sha256, process.ImageSha256,
            StringComparison.OrdinalIgnoreCase))
        return false;
      process.VerifyPeer(process.Identity.Pid);
      return true;
    }
    catch (Exception error) when (error is IOException or InvalidDataException or
      ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
    { return false; }
  }

  private async Task StopAndObserveExitAsync(SupervisedProcess process)
  {
    while (true)
    {
      try
      {
        if (process.HasExited) return;
        if (CandidateStop is { } stop) await stop(process).ConfigureAwait(false);
        else await process.StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (process.HasExited) return;
      }
      catch (Exception stopError)
      {
        _unresolvedFailure.TrySetResult(new IOException(
          "The candidate stop failed while native exit remains unobserved.", stopError));
      }
      await ObserveExitAsync(process).ConfigureAwait(false);
      return;
    }
  }

  private static async Task StopAndObserveExitAsync(UnverifiedChild child)
  {
    while (true)
    {
      try
      {
        if (child.HasExited) return;
        await child.StopAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
        if (child.HasExited) return;
      }
      catch { }
      await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
    }
  }

  private static async Task ObserveExitAsync(SupervisedProcess process)
  {
    while (true)
    {
      try
      {
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        if (process.HasExited) return;
      }
      catch { }
      await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
    }
  }

  internal static UpdateOutcome? CheckConfirmation(ConfirmedUpdate update)
  {
    ArgumentNullException.ThrowIfNull(update);
    if (update.UserConfirmationId == Guid.Empty || update.OldTarget is null ||
        update.ManifestSha256 is not { Length: 64 } ||
        !update.ManifestSha256.All(Uri.IsHexDigit))
      throw new ArgumentException("The update confirmation is incomplete.", nameof(update));
    var selected = ReleaseSelector.Select([update.Release]);
    if (!selected.Installable || selected.Identity is null ||
        !selected.Identity.Equals(update.ReleaseIdentity))
      return new UpdateOutcome(UpdateOutcomeKind.Deferred, "Release confirmation changed.");
    var oldVersion = SemanticVersion.ParseTag("v" + update.OldTarget.Version);
    if (update.Release.Version.CompareTo(oldVersion) <= 0)
      return new UpdateOutcome(UpdateOutcomeKind.Deferred, "The release is not newer than the installed version.");
    return null;
  }
}
