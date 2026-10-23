using System.Runtime.Versioning;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Transport;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Handover;

public sealed record ConfirmedUpdate(ReleaseCandidate Release, ReleaseIdentity ReleaseIdentity,
  string ManifestSha256, OwnedOrOriginalTarget OldTarget, Guid UserConfirmationId);

public enum UpdateOutcomeKind { Prepared, Committed, Cancelled, Deferred, Refused, Failed }

public sealed record UpdateOutcome(UpdateOutcomeKind Kind, string? Reason = null,
  bool FallbackLaunched = false);

// The caller keeps Admission until its process exits so no job starts before the handover.
public sealed record PreparedUpdate(UpdateOutcome Outcome, Guid TransactionId = default,
  IDisposable? Admission = null);

public sealed class UpdateDeferredException(string message) : Exception(message);

[SupportedOSPlatform("windows")]
public sealed class UpdatePreparation(
  AtomicStateStore state, ISignedReleaseReader releases, PackageStager stager,
  UpdateJournal journal, CurrentTargetStore targets, IActivityLeaseStore activity,
  OwnedDirectoryStore directories)
{
  public async Task<PreparedUpdate> PrepareUpdateAsync(ConfirmedUpdate update,
    InstallationLayout layout, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(layout);
    if (CheckConfirmation(update) is { } refusal) return new PreparedUpdate(refusal);
    var admission = activity.TryEnterUpdate();
    if (admission is null)
      return new PreparedUpdate(new UpdateOutcome(UpdateOutcomeKind.Deferred,
        "An application job or another update is active."));
    var operation = Guid.NewGuid();
    var begun = false;
    StagedPackage? staged = null;
    PromotedPackage? promoted = null;
    try
    {
      layout.Validate(directories);
      RequireStableLauncher(layout);
      var prior = targets.CapturePriorMapping(update.OldTarget);
      var settingsPath = Path.Combine(layout.DataRoot, "settings.json");
      var settings = await SettingsSnapshot.CaptureAsync(settingsPath, cancellationToken)
        .ConfigureAwait(false);
      var manifest = await releases.ReadAsync(update.Release, settings.SchemaVersion ?? 1,
        cancellationToken).ConfigureAwait(false);
      if (!string.Equals(manifest.ManifestSha256, update.ManifestSha256,
          StringComparison.OrdinalIgnoreCase))
        throw new UpdateDeferredException("The signed manifest changed after confirmation.");
      journal.Begin(new UpdateTransaction(1, layout.InstallationId, operation,
        UpdatePhase.Preparing, update.OldTarget, null, settings.Sha256, settings.IsAbsent,
        UpdateProtocol.ProtocolVersion, Convert.ToHexStringLower(UpdatePipe.NewNonce()), [],
        null, null, prior));
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
      return new PreparedUpdate(new UpdateOutcome(UpdateOutcomeKind.Prepared), operation,
        admission);
    }
    catch (Exception error)
    {
      if (begun) TryFail(operation);
      admission.Dispose();
      return new PreparedUpdate(new UpdateOutcome(
        error is UpdateDeferredException ? UpdateOutcomeKind.Deferred :
        error is OperationCanceledException && cancellationToken.IsCancellationRequested ?
          UpdateOutcomeKind.Cancelled : UpdateOutcomeKind.Failed, error.Message));
    }
    finally
    {
      promoted?.Dispose();
      staged?.Dispose();
    }
  }

  public async Task<PreparedUpdate> PrepareRollbackAsync(InstallationLayout layout,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(layout);
    var admission = activity.TryEnterUpdate();
    if (admission is null)
      return new PreparedUpdate(new UpdateOutcome(UpdateOutcomeKind.Deferred,
        "An application job or another update is active."));
    var operation = Guid.NewGuid();
    var begun = false;
    try
    {
      layout.Validate(directories);
      RequireStableLauncher(layout);
      var snapshot = JournalSnapshot.Read(state, layout.Records.Path);
      var installation = snapshot.Installation.Value!;
      if (snapshot.Current.Kind != StateReadKind.Valid ||
          snapshot.Current.Value is not { Previous: { } previous } current ||
          snapshot.Ownership.Value is not { } ownership ||
          previous.ProtocolVersion != UpdateProtocol.ProtocolVersion ||
          previous.Package is null ||
          !RecoveryPolicy.TargetBelongs(previous, installation, ownership))
      {
        admission.Dispose();
        return new PreparedUpdate(new UpdateOutcome(UpdateOutcomeKind.Refused,
          "No previous version can be restored automatically. The original BookSplice " +
          "folder remains available for manual use: " + installation.OriginalApplicationPath));
      }
      using (LaunchResolver.OpenTarget(layout, installation, previous)) { }
      var settings = await SettingsSnapshot.CaptureAsync(
        Path.Combine(layout.DataRoot, "settings.json"), cancellationToken).ConfigureAwait(false);
      journal.Begin(new UpdateTransaction(1, layout.InstallationId, operation,
        UpdatePhase.Preparing, current.Current, previous, settings.Sha256, settings.IsAbsent,
        UpdateProtocol.ProtocolVersion, Convert.ToHexStringLower(UpdatePipe.NewNonce()), [],
        null, null, current));
      begun = true;
      journal.Transition(operation, UpdatePhase.Preparing, UpdatePhase.Prepared);
      return new PreparedUpdate(new UpdateOutcome(UpdateOutcomeKind.Prepared), operation,
        admission);
    }
    catch (Exception error)
    {
      if (begun) TryFail(operation);
      admission.Dispose();
      return new PreparedUpdate(new UpdateOutcome(
        error is OperationCanceledException && cancellationToken.IsCancellationRequested ?
          UpdateOutcomeKind.Cancelled : UpdateOutcomeKind.Failed, error.Message));
    }
  }

  private void RequireStableLauncher(InstallationLayout layout)
  {
    var installation = state.Read<InstallationRecord>(
      Path.Combine(layout.Records.Path, "installation.json"));
    if (installation.Kind != StateReadKind.Valid || installation.Value is not { } record ||
        record.InstallationId != layout.InstallationId)
      throw new InvalidDataException("The installation record is invalid.");
    StableLauncherBootstrap.RequireRecorded(layout, record);
  }

  private void TryFail(Guid operation)
  {
    try { journal.Transition(operation, UpdatePhase.Preparing, UpdatePhase.Failed); }
    catch (Exception error) when (error is IOException or InvalidDataException or
      UnauthorizedAccessException)
    { }
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
