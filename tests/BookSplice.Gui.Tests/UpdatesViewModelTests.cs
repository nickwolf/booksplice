using BookSplice.Gui.Updates;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Policy;

namespace BookSplice.Gui.Tests;

public sealed class UpdatesViewModelTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(), "booksplice-updates-vm-" + Guid.NewGuid().ToString("N"));
  private readonly Clock _clock = new();
  private readonly FakeBackend _backend = new();
  private int _queue;
  private int _shutdowns;

  public void Dispose()
  {
    for (var attempt = 0; Directory.Exists(_root); attempt++)
    {
      try { Directory.Delete(_root, true); }
      catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
    }
  }

  private UpdatesViewModel Create(System.Windows.Threading.Dispatcher dispatcher, out GuiOperationGate gate)
  {
    gate = new GuiOperationGate(dispatcher);
    return new UpdatesViewModel(_backend, new UpdatePreferencesStore(_root), new UpdatePreferences(), "0.1.0",
      @"C:\Apps\BookSplice", gate, () => _queue, () => _shutdowns++, _clock);
  }

  [Fact]
  public void DefaultsAndManualCheckSetAvailableDetailsWithoutADiscoveryNotice() => DispatcherTestHost.Run(async dispatcher =>
  {
    var model = Create(dispatcher, out _);
    Assert.Equal(UpdatesPhase.Idle, model.Phase);
    Assert.True(model.AutomaticChecksEnabled);
    Assert.Equal("never", model.LastSuccessText);
    var notices = 0;
    model.Discovered += (_, _) => notices++;
    _backend.Result = Available("v0.2.0", "Fixed things.");
    await model.CheckAsync(manual: true);
    await model.Saved;
    Assert.Equal(UpdatesPhase.Available, model.Phase);
    Assert.Equal("0.2.0", model.AvailableVersion);
    Assert.Equal("Fixed things.", model.ReleaseNotes);
    Assert.NotNull(model.ReleaseUrl);
    Assert.True(model.CanInstall);
    Assert.Equal(0, notices);
    Assert.Equal(_clock.GetUtcNow(), new UpdatePreferencesStore(_root).Load().LastSuccess);
  });

  [Fact]
  public void AutomaticDiscoveryNotifiesOncePerVersion() => DispatcherTestHost.Run(async dispatcher =>
  {
    var model = Create(dispatcher, out _);
    var notices = 0;
    model.Discovered += (_, _) => notices++;
    _backend.Result = Available("v0.2.0", "a");
    await model.RunDueCheckAsync(CancellationToken.None);
    await model.RunDueCheckAsync(CancellationToken.None);
    Assert.Equal(1, notices);
    _backend.Result = Available("v0.3.0", "b");
    await model.RunDueCheckAsync(CancellationToken.None);
    await model.Saved;
    Assert.Equal(2, notices);
    Assert.Equal("v0.3.0", new UpdatePreferencesStore(_root).Load().NotifiedTag);
  });

  [Fact]
  public void FailuresAreNeverReportedAsUpToDate() => DispatcherTestHost.Run(async dispatcher =>
  {
    var model = Create(dispatcher, out _);
    _backend.Result = new CheckResult(CheckKind.Success, new SelectionResult(SelectionKind.None, null, false, null, null), null, null);
    await model.CheckAsync(manual: true);
    Assert.Equal(UpdatesPhase.UpToDate, model.Phase);
    foreach (var result in new[]
    {
      new CheckResult(CheckKind.TransportFailure, null, "offline", null),
      new CheckResult(CheckKind.RateLimited, null, "limited", _clock.GetUtcNow().AddHours(2)),
      new CheckResult(CheckKind.UnavailableAsset, null, "missing assets", null),
      new CheckResult(CheckKind.Ambiguous, null, "two releases", null)
    })
    {
      _backend.Result = result;
      await model.CheckAsync(manual: true);
      Assert.Equal(UpdatesPhase.CheckFailed, model.Phase);
      Assert.DoesNotContain("up to date", model.Message);
    }
    _backend.Result = Available("v0.2.0", "n");
    _backend.ManifestFailure = new InvalidDataException("bad signature");
    await model.CheckAsync(manual: true);
    Assert.Equal(UpdatesPhase.CheckFailed, model.Phase);
    Assert.False(model.CanInstall);
  });

  [Fact]
  public void NotesAreBoundedPlainTextAndOnlyRepositoryReleaseLinksAreOffered() => DispatcherTestHost.Run(async dispatcher =>
  {
    var model = Create(dispatcher, out _);
    _backend.Result = Available("v0.2.0", "<b>x</b>\u0007" + new string('y', 20_000));
    await model.CheckAsync(manual: true);
    Assert.StartsWith("<b>x</b>y", model.ReleaseNotes);
    Assert.InRange(model.ReleaseNotes.Length, 1, 8003);
    _backend.Result = Available("v0.2.0", "n", new Uri("https://example.com/evil"));
    await model.CheckAsync(manual: true);
    Assert.Null(model.ReleaseUrl);
  });

  [Fact]
  public void AutomaticScheduleFollowsTheExistingRules() => DispatcherTestHost.Run(async dispatcher =>
  {
    var model = Create(dispatcher, out _);
    _backend.Result = new CheckResult(CheckKind.Success, new SelectionResult(SelectionKind.None, null, false, null, null), null, null);
    Assert.Equal(_clock.GetUtcNow().AddHours(24), await model.RunDueCheckAsync(CancellationToken.None));
    _backend.Result = new CheckResult(CheckKind.TransportFailure, null, "offline", null);
    Assert.Equal(_clock.GetUtcNow().AddHours(1), await model.RunDueCheckAsync(CancellationToken.None));
    model.AutomaticChecksEnabled = false;
    await model.Saved;
    Assert.False(new UpdatePreferencesStore(_root).Load().AutomaticChecksEnabled);
  });

  [Fact]
  public void QueuedRowsDeferInstallationWithTheExactCopyAndChangeNothing() => DispatcherTestHost.Run(async dispatcher =>
  {
    var model = Create(dispatcher, out var gate);
    _backend.Result = Available("v0.2.0", "n");
    await model.CheckAsync(manual: true);
    _queue = 1;
    await model.InstallAsync();
    Assert.Equal(UpdatesPhase.Deferred, model.Phase);
    Assert.Equal("Remove queued books before installing. Reviewed edits remain in this window until you remove them.", model.Message);
    Assert.Equal(0, _backend.Installs);
    Assert.Equal(0, _shutdowns);
    Assert.Equal(1, _queue);
    Assert.False(gate.IsFrozen);
  });

  [Fact]
  public void InstallFreezesTheGateRestartsAndUsesTheExactShownMetadata() => DispatcherTestHost.Run(async dispatcher =>
  {
    var model = Create(dispatcher, out var gate);
    _backend.Result = Available("v0.2.0", "n");
    await model.CheckAsync(manual: true);
    var shown = _backend.Result.Selection!;
    _backend.InstallResult = new UpdateOutcome(UpdateOutcomeKind.Prepared);
    _backend.OnInstall = () => Assert.True(gate.IsFrozen);
    await model.InstallAsync();
    Assert.Equal(UpdatesPhase.Restarting, model.Phase);
    Assert.Equal(1, _shutdowns);
    Assert.True(gate.IsFrozen);
    Assert.Same(shown.Identity, _backend.InstalledIdentity);
    Assert.Equal(shown.Candidate!.Tag, _backend.InstalledRelease!.Tag);
    Assert.Equal(_backend.Digest, _backend.InstalledDigest);
    Assert.False(model.CanCheck);
  });

  [Fact]
  public void FailureAndCancellationRestoreTheUi() => DispatcherTestHost.Run(async dispatcher =>
  {
    var model = Create(dispatcher, out var gate);
    _backend.Result = Available("v0.2.0", "n");
    await model.CheckAsync(manual: true);
    _backend.InstallResult = new UpdateOutcome(UpdateOutcomeKind.Failed, "launcher missing");
    await model.InstallAsync();
    Assert.Equal(UpdatesPhase.Failed, model.Phase);
    Assert.Contains("launcher missing", model.Message);
    Assert.False(gate.IsFrozen);
    Assert.True(model.CanInstall);
    Assert.Equal(0, _shutdowns);
    _backend.InstallResult = null;
    var install = model.InstallAsync();
    Assert.Equal(UpdatesPhase.Preparing, model.Phase);
    Assert.True(model.CanCancel);
    model.CancelDownload();
    await install;
    Assert.Equal(UpdatesPhase.Cancelled, model.Phase);
    Assert.False(gate.IsFrozen);
    Assert.Equal(0, _shutdowns);
  });

  [Fact]
  public void ChangedMetadataReturnsToAvailableWithFreshDetailsAndNeedsANewClick() => DispatcherTestHost.Run(async dispatcher =>
  {
    var model = Create(dispatcher, out var gate);
    _backend.Result = Available("v0.2.0", "old notes");
    await model.CheckAsync(manual: true);
    _backend.InstallResult = new UpdateOutcome(UpdateOutcomeKind.Deferred, "The signed manifest changed after confirmation.");
    _backend.OnInstall = () => { _backend.Result = Available("v0.2.0", "new notes"); _backend.Digest = new string('b', 64); };
    await model.InstallAsync();
    Assert.Equal(UpdatesPhase.Available, model.Phase);
    Assert.Equal("new notes", model.ReleaseNotes);
    Assert.Contains("changed", model.Message);
    Assert.False(gate.IsFrozen);
    Assert.Equal(0, _shutdowns);
    _backend.OnInstall = null;
    _backend.InstallResult = new UpdateOutcome(UpdateOutcomeKind.Prepared);
    await model.InstallAsync();
    Assert.Equal(new string('b', 64), _backend.InstalledDigest);
    Assert.Equal(1, _shutdowns);
  });

  [Fact]
  public void RollbackNeedsARecordedPreviousAndTheSameEmptyQueueGate() => DispatcherTestHost.Run(async dispatcher =>
  {
    var model = Create(dispatcher, out var gate);
    await model.InitializeAsync();
    Assert.False(model.CanRollbackNow);
    await model.RollbackAsync();
    Assert.Equal(0, _backend.Rollbacks);
    _backend.Previous = true;
    await model.InitializeAsync();
    Assert.True(model.CanRollbackNow);
    _queue = 2;
    await model.RollbackAsync();
    Assert.Equal(UpdatesViewModel.QueueDeferral, model.Message);
    Assert.Equal(0, _backend.Rollbacks);
    _queue = 0;
    await model.RollbackAsync();
    Assert.Equal(UpdatesPhase.Restarting, model.Phase);
    Assert.Equal(1, _backend.Rollbacks);
    Assert.Equal(1, _shutdowns);
    Assert.True(gate.IsFrozen);
  });

  private static CheckResult Available(string tag, string notes, Uri? page = null)
  {
    var uri = page ?? new Uri("https://github.com/nickwolf/booksplice/releases/tag/" + tag);
    var candidate = new ReleaseCandidate(1, tag, SemanticVersion.ParseTag(tag), uri, notes, false, false, []);
    return new CheckResult(CheckKind.Success, new SelectionResult(SelectionKind.Available, candidate, true, null,
      new ReleaseIdentity(1, tag, uri, notes, [])), null, null);
  }

  private sealed class Clock : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
  }

  private sealed class FakeBackend : IUpdateBackend
  {
    public CheckResult Result { get; set; } = new(CheckKind.Skipped, null, null, null);
    public Exception? ManifestFailure { get; set; }
    public string Digest { get; set; } = new string('a', 64);
    public bool Previous { get; set; }
    public UpdateOutcome? InstallResult { get; set; }
    public Action? OnInstall { get; set; }
    public int Installs { get; private set; }
    public int Rollbacks { get; private set; }
    public ReleaseCandidate? InstalledRelease { get; private set; }
    public ReleaseIdentity? InstalledIdentity { get; private set; }
    public string? InstalledDigest { get; private set; }

    public Task<CheckResult> CheckAsync(bool manual, CancellationToken cancellationToken) => Task.FromResult(Result);

    public Task<string> ReadManifestDigestAsync(ReleaseCandidate release, CancellationToken cancellationToken) =>
      ManifestFailure is null ? Task.FromResult(Digest) : Task.FromException<string>(ManifestFailure);

    public bool HasPrevious() => Previous;

    public async Task<UpdateOutcome> InstallAsync(ReleaseCandidate release, ReleaseIdentity identity, string manifestSha256,
      Action shutdown, CancellationToken cancellationToken)
    {
      Installs++;
      (InstalledRelease, InstalledIdentity, InstalledDigest) = (release, identity, manifestSha256);
      OnInstall?.Invoke();
      if (InstallResult is null) await Task.Delay(Timeout.Infinite, cancellationToken);
      if (InstallResult!.Kind == UpdateOutcomeKind.Prepared) shutdown();
      return InstallResult;
    }

    public Task<UpdateOutcome> RollbackAsync(Action shutdown, CancellationToken cancellationToken)
    {
      Rollbacks++;
      shutdown();
      return Task.FromResult(new UpdateOutcome(UpdateOutcomeKind.Prepared));
    }
  }
}
