using System.IO;
using System.Net.Http;
using System.Text;
using BookSplice.Gui.ViewModels;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Transport;

namespace BookSplice.Gui.Updates;

public interface IUpdateBackend
{
  Task<CheckResult> CheckAsync(bool manual, CancellationToken cancellationToken);
  Task<string> ReadManifestDigestAsync(ReleaseCandidate release, CancellationToken cancellationToken);
  bool HasPrevious();
  Task<UpdateOutcome> InstallAsync(ReleaseCandidate release, ReleaseIdentity identity,
    string manifestSha256, Action shutdown, CancellationToken cancellationToken);
  Task<UpdateOutcome> RollbackAsync(Action shutdown, CancellationToken cancellationToken);
}

public enum UpdatesPhase { Idle, Checking, UpToDate, Available, Deferred, Preparing, Restarting, Cancelled, CheckFailed, Failed }

// All state changes happen on the Dispatcher: callers await these methods from UI code.
public sealed class UpdatesViewModel : ObservableModel, IDisposable
{
  public const string QueueDeferral = "Remove queued books before installing. Reviewed edits remain in this window until you remove them.";
  private const int NotesLimit = 8000;
  private readonly IUpdateBackend _backend;
  private readonly UpdatePreferencesStore _store;
  private readonly GuiOperationGate _gate;
  private readonly Func<int> _queueCount;
  private readonly Action _shutdown;
  private readonly TimeProvider _clock;
  private readonly SemanticVersion? _installed;
  private UpdatePreferences _preferences;
  private ReleaseCandidate? _candidate;
  private ReleaseIdentity? _identity;
  private string? _manifestSha256;
  private IDisposable? _freeze;
  private CancellationTokenSource? _operation;
  private CancellationTokenSource? _schedule;
  private UpdatesPhase _phase;
  private string _message = "";
  private bool _canRollback;
  private Task _saved = Task.CompletedTask;

  public UpdatesViewModel(IUpdateBackend backend, UpdatePreferencesStore store, UpdatePreferences preferences,
    string installedVersion, string installLocation, GuiOperationGate gate, Func<int> queueCount, Action shutdown,
    TimeProvider? clock = null)
  {
    _backend = backend;
    _store = store;
    _preferences = preferences;
    InstalledVersion = installedVersion;
    InstallLocationText = $"BookSplice installs the new version in a new folder beside {installLocation} and keeps the current folder. " +
      "It adds a BookSplice shortcut to the Start Menu unless one already exists, and leaves any other shortcut alone.";
    _gate = gate;
    _queueCount = queueCount;
    _shutdown = shutdown;
    _clock = clock ?? TimeProvider.System;
    try { _installed = SemanticVersion.ParseTag("v" + installedVersion); }
    catch (FormatException) { }
  }

  public event EventHandler? Discovered;

  public string InstalledVersion { get; }
  public string InstallLocationText { get; }
  public UpdatesPhase Phase { get => _phase; private set { _phase = value; Changed(); Changed(nameof(CanInstall)); Changed(nameof(CanCheck)); Changed(nameof(CanCancel)); Changed(nameof(HasAvailable)); Changed(nameof(CanRollbackNow)); } }
  public string Message { get => _message; private set { _message = value; Changed(); } }
  public string? AvailableVersion { get; private set; }
  public string ReleaseNotes { get; private set; } = "";
  public Uri? ReleaseUrl { get; private set; }
  public bool HasReleaseUrl => ReleaseUrl is not null;
  public bool HasAvailable => _candidate is not null;
  public bool CanInstall => HasAvailable && CanCheck;
  public bool CanCheck => Phase is not (UpdatesPhase.Checking or UpdatesPhase.Preparing or UpdatesPhase.Restarting);
  public bool CanCancel => Phase == UpdatesPhase.Preparing;
  public bool CanRollbackNow => _canRollback && CanCheck;
  public string LastSuccessText => _preferences.LastSuccess is { } value ? value.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) : "never";

  public bool AutomaticChecksEnabled
  {
    get => _preferences.AutomaticChecksEnabled;
    set
    {
      if (value == _preferences.AutomaticChecksEnabled) return;
      Save(_preferences with { AutomaticChecksEnabled = value });
      Changed();
      if (value) StartSchedule(); else StopSchedule();
    }
  }

  public async Task InitializeAsync()
  {
    _canRollback = await Task.Run(_backend.HasPrevious);
    Changed(nameof(CanRollbackNow));
  }

  public async Task CheckAsync(bool manual, CancellationToken cancellationToken = default)
  {
    if (!CanCheck) return;
    var before = Phase;
    var beforeMessage = Message;
    Phase = UpdatesPhase.Checking;
    Message = "Checking for updates.";
    CheckResult result;
    try { result = await _backend.CheckAsync(manual, cancellationToken); }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Phase = before; Message = beforeMessage; return; }
    catch (Exception exception)
    { Fail(UpdatesPhase.CheckFailed, "Could not check for updates: " + exception.Message + " Try again later."); return; }
    switch (result.Kind)
    {
      case CheckKind.Skipped: Phase = before; Message = beforeMessage; return;
      case CheckKind.Success: break;
      case CheckKind.UnavailableAsset or CheckKind.Ambiguous:
        Fail(UpdatesPhase.CheckFailed, "A newer release exists but cannot be installed automatically: " + result.Error);
        return;
      default:
        Fail(UpdatesPhase.CheckFailed, "Could not check for updates: " + result.Error + " BookSplice will retry; you can also check again now.");
        return;
    }
    Save(_preferences with { LastSuccess = _clock.GetUtcNow() });
    Changed(nameof(LastSuccessText));
    if (_installed is null)
    {
      Fail(UpdatesPhase.CheckFailed, "The installed version could not be read, so updates cannot be compared.");
      return;
    }
    if (result.Selection is not { Kind: SelectionKind.Available, Installable: true, Candidate: { } candidate, Identity: { } identity } ||
        candidate.Version.CompareTo(_installed) <= 0)
    {
      Clear();
      Phase = UpdatesPhase.UpToDate;
      Message = $"BookSplice {InstalledVersion} is up to date.";
      return;
    }
    string digest;
    try { digest = await _backend.ReadManifestDigestAsync(candidate, cancellationToken); }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Phase = before; Message = beforeMessage; return; }
    catch (Exception exception)
    { Fail(UpdatesPhase.CheckFailed, "A newer release exists but its signed update could not be verified: " + exception.Message); return; }
    _candidate = candidate;
    _identity = identity;
    _manifestSha256 = digest;
    AvailableVersion = candidate.Tag[1..];
    ReleaseNotes = BoundNotes(candidate.Notes);
    ReleaseUrl = ValidatedHttpTransport.MatchesReleaseTagPath(candidate.PageUri, candidate.Tag) ? candidate.PageUri : null;
    Changed(nameof(AvailableVersion));
    Changed(nameof(ReleaseNotes));
    Changed(nameof(ReleaseUrl));
    Changed(nameof(HasReleaseUrl));
    Phase = UpdatesPhase.Available;
    Message = $"BookSplice {AvailableVersion} is available.";
    if (!manual && _preferences.NotifiedTag != candidate.Tag)
    {
      Save(_preferences with { NotifiedTag = candidate.Tag });
      Discovered?.Invoke(this, EventArgs.Empty);
    }
  }

  public async Task InstallAsync(CancellationToken cancellationToken = default)
  {
    if (!CanInstall || _candidate is not { } candidate || _identity is not { } identity || _manifestSha256 is not { } digest) return;
    var outcome = await RunExclusiveAsync(token => _backend.InstallAsync(candidate, identity, digest, _shutdown, token),
      "Preparing the update. You can cancel until the restart begins.", "Restarting to finish the update.", cancellationToken);
    if (outcome is { Kind: UpdateOutcomeKind.Deferred, Reason: { } reason })
    {
      // The release may have changed after the details were shown, so show fresh details and require a new click.
      await CheckAsync(manual: true, cancellationToken);
      if (Phase == UpdatesPhase.Available) Message = reason + " Review the details and choose Install and restart again.";
    }
  }

  public Task RollbackAsync(CancellationToken cancellationToken = default) => !CanRollbackNow ? Task.CompletedTask :
    RunExclusiveAsync(token => _backend.RollbackAsync(_shutdown, token),
      "Preparing to roll back.", "Restarting to roll back to the previous version.", cancellationToken);

  internal Task Saved => _saved;

  public void CancelDownload() => _operation?.Cancel();

  private async Task<UpdateOutcome?> RunExclusiveAsync(Func<CancellationToken, Task<UpdateOutcome>> run,
    string workingMessage, string restartMessage, CancellationToken cancellationToken)
  {
    var count = _queueCount();
    var freeze = _gate.TryFreeze(count);
    if (freeze is null)
    {
      Phase = UpdatesPhase.Deferred;
      Message = count > 0 ? QueueDeferral : "Wait for active work to finish before updating.";
      return null;
    }
    using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    _operation = operation;
    Phase = UpdatesPhase.Preparing;
    Message = workingMessage;
    UpdateOutcome outcome;
    try { outcome = await run(operation.Token); }
    catch (OperationCanceledException) when (operation.IsCancellationRequested) { outcome = new UpdateOutcome(UpdateOutcomeKind.Cancelled); }
    catch (Exception exception)
    { outcome = new UpdateOutcome(UpdateOutcomeKind.Failed, exception.Message); }
    finally { _operation = null; }
    if (outcome.Kind == UpdateOutcomeKind.Prepared)
    {
      _freeze = freeze;
      Phase = UpdatesPhase.Restarting;
      Message = restartMessage;
      return outcome;
    }
    freeze.Dispose();
    switch (outcome.Kind)
    {
      case UpdateOutcomeKind.Cancelled:
        Phase = UpdatesPhase.Cancelled;
        Message = "Update cancelled. Nothing was changed.";
        break;
      case UpdateOutcomeKind.Deferred:
        Phase = UpdatesPhase.Deferred;
        Message = outcome.Reason ?? "The update was deferred.";
        break;
      default:
        Phase = UpdatesPhase.Failed;
        Message = "The update did not start: " + outcome.Reason + " BookSplice is still usable and nothing was removed.";
        break;
    }
    return outcome;
  }

  public void StartSchedule()
  {
    if (_schedule is not null || !AutomaticChecksEnabled) return;
    _schedule = new CancellationTokenSource();
    _ = RunScheduleAsync(_schedule.Token);
  }

  public void StopSchedule()
  {
    _schedule?.Cancel();
    _schedule = null;
  }

  // The first automatic check runs 60 seconds after startup when none succeeded in the last 24 hours.
  private async Task RunScheduleAsync(CancellationToken token)
  {
    try
    {
      var due = CheckSchedule.NextDue(_clock.GetUtcNow(), _preferences.LastSuccess, null);
      while (!token.IsCancellationRequested)
      {
        var wait = due - _clock.GetUtcNow();
        if (wait > TimeSpan.Zero) await Task.Delay(wait, _clock, token);
        due = await RunDueCheckAsync(token);
      }
    }
    catch (OperationCanceledException) { }
  }

  // Runs one automatic check and returns when the next one is due.
  internal async Task<DateTimeOffset> RunDueCheckAsync(CancellationToken token)
  {
    await CheckAsync(manual: false, token);
    var now = _clock.GetUtcNow();
    return Phase == UpdatesPhase.CheckFailed ? CheckSchedule.NextFailureDue(now, null) :
      CheckSchedule.NextDue(now, _preferences.LastSuccess, null);
  }

  private void Fail(UpdatesPhase phase, string message)
  {
    Clear();
    Phase = phase;
    Message = message;
  }

  private void Clear()
  {
    _candidate = null;
    _identity = null;
    _manifestSha256 = null;
    AvailableVersion = null;
    ReleaseNotes = "";
    ReleaseUrl = null;
    Changed(nameof(AvailableVersion));
    Changed(nameof(ReleaseNotes));
    Changed(nameof(ReleaseUrl));
    Changed(nameof(HasReleaseUrl));
  }

  private void Save(UpdatePreferences value)
  {
    _preferences = value;
    _saved = _saved.ContinueWith(_ => _store.SaveAsync(value), TaskScheduler.Default).Unwrap()
      .ContinueWith(task => { _ = task.Exception; }, TaskScheduler.Default);
  }

  internal static string BoundNotes(string notes)
  {
    var text = new StringBuilder();
    foreach (var character in notes)
    {
      if (text.Length >= NotesLimit) { text.Append("..."); break; }
      if (!char.IsControl(character) || character is '\n' or '\t') text.Append(character);
    }
    return text.ToString().Replace("\r", "");
  }

  public void Dispose()
  {
    StopSchedule();
    _operation?.Cancel();
    _freeze?.Dispose();
  }
}
