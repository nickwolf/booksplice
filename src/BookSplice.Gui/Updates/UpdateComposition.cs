using System.Globalization;
using System.IO;
using BookSplice.Core.Settings;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Security;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Transport;
using BookSplice.Updates.Windows;

namespace BookSplice.Gui.Updates;

// Wires the production update components once and owns their lifetimes.
public sealed class UpdateComposition : IUpdateBackend, IDisposable
{
  private readonly string _dataRoot;
  private readonly string _applicationDirectory;
  private readonly string? _version;
  private readonly GitHubReleaseClient _client;
  private readonly ValidatedHttpTransport _transport;
  private readonly UpdateCheckService _checks;
  private readonly object _openGate = new();
  private IDisposable? _admission;

  public UpdateComposition(string dataRoot, string applicationDirectory, string? packagedVersion,
    ICheckState checkState, TimeProvider? clock = null)
  {
    _dataRoot = dataRoot;
    _applicationDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
    _version = packagedVersion;
    clock ??= TimeProvider.System;
    _client = new GitHubReleaseClient(clock);
    _transport = ValidatedHttpTransport.CreateProduction(clock);
    Reader = new SignedReleaseReader(_client, _transport, new ManifestVerifier());
    _checks = new UpdateCheckService(_client, checkState, clock);
  }

  public ISignedReleaseReader Reader { get; }

  public Task<CheckResult> CheckAsync(bool manual, CancellationToken cancellationToken) =>
    _checks.CheckAsync(manual, cancellationToken);

  public async Task<string> ReadManifestDigestAsync(ReleaseCandidate release,
    CancellationToken cancellationToken) =>
    (await Reader.ReadAsync(release, AppSettings.CurrentSchemaVersion, cancellationToken)).ManifestSha256!;

  public static LaunchedImage Self()
  {
    using var self = SupervisedProcess.CaptureCurrent();
    return new LaunchedImage(self.Identity.ExecutablePath, self.ImageIdentity);
  }

  // The committed image for this executable's installation, or null when it has none.
  public LaunchedImage? Mapped() => TryOpen() is { } opened ? Current(opened) is { } current ?
    new LaunchedImage(current.ExecutablePath, current.ExecutableIdentity) : null : null;

  public bool TryForwardDirectLaunch() => GuiStartup.TryForward(Self(), Mapped, () =>
  {
    var opened = TryOpen()!;
    StableLauncherBootstrap.Start(opened.Layout, opened.Record, []).Dispose();
  });

  public bool HasPrevious() => TryOpen() is { } opened &&
    opened.State.Read<CurrentTarget>(Path.Combine(opened.RecordsPath, "current.json"))
      .Value?.Previous is not null;

  public Task<UpdateOutcome> InstallAsync(ReleaseCandidate release, ReleaseIdentity identity,
    string manifestSha256, Action shutdown, CancellationToken cancellationToken) =>
    HandOffAsync(async () =>
    {
      var opened = await Task.Run(() => FindInstallation() ?? Create(), cancellationToken);
      var old = Current(opened) ?? throw new InvalidDataException("The installation has no current version.");
      var prepared = await Preparation(opened).PrepareUpdateAsync(new ConfirmedUpdate(release,
        identity, manifestSha256, old, Guid.NewGuid()), opened.Layout, cancellationToken);
      return (prepared, opened);
    }, shutdown);

  public Task<UpdateOutcome> RollbackAsync(Action shutdown, CancellationToken cancellationToken) =>
    HandOffAsync(async () =>
    {
      var opened = FindInstallation() ?? throw new InvalidDataException("No previous version is recorded.");
      return (await Preparation(opened).PrepareRollbackAsync(opened.Layout, cancellationToken), opened);
    }, shutdown);

  private async Task<UpdateOutcome> HandOffAsync(
    Func<Task<(PreparedUpdate Prepared, OpenedInstallation Opened)>> prepare, Action shutdown)
  {
    PreparedUpdate prepared;
    OpenedInstallation opened;
    try { (prepared, opened) = await prepare(); }
    catch (Exception error) when (error is IOException or InvalidDataException or
      UnauthorizedAccessException or NotSupportedException or ArgumentException or
      System.ComponentModel.Win32Exception)
    {
      return new UpdateOutcome(UpdateOutcomeKind.Failed, error.Message);
    }
    return HandOff(prepared, () =>
    {
      using var self = SupervisedProcess.CaptureCurrent();
      StableLauncherBootstrap.Start(opened.Layout, opened.Record, ["--apply",
        prepared.TransactionId.ToString("D"),
        self.Identity.Pid.ToString(CultureInfo.InvariantCulture),
        self.Identity.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture)]).Dispose();
    }, () => opened.Journal.Transition(prepared.TransactionId, UpdatePhase.Prepared, UpdatePhase.Failed),
      admission => _admission = admission, shutdown);
  }

  // After Prepared: start the launcher, keep the admission until this process exits, then shut down.
  // If the launcher cannot start, the transaction is marked Failed and the admission is released.
  internal static UpdateOutcome HandOff(PreparedUpdate prepared, Action startLauncher,
    Action markFailed, Action<IDisposable> holdAdmission, Action shutdown)
  {
    if (prepared.Outcome.Kind != UpdateOutcomeKind.Prepared) return prepared.Outcome;
    try { startLauncher(); }
    catch (Exception error)
    {
      try { markFailed(); } catch (Exception) { }
      prepared.Admission?.Dispose();
      return new UpdateOutcome(UpdateOutcomeKind.Failed,
        "The update could not be handed to the launcher: " + error.Message);
    }
    if (prepared.Admission is not null) holdAdmission(prepared.Admission);
    shutdown();
    return prepared.Outcome;
  }

  private UpdatePreparation Preparation(OpenedInstallation opened) => new(opened.State, Reader,
    new PackageStager(new PackageDownloader(_client, _transport), opened.Directories, opened.Journal),
    opened.Journal, new CurrentTargetStore(opened.State, opened.Journal, opened.Layout),
    new ActivityLeaseStore(opened.Layout.DataRoot), opened.Directories);

  private OpenedInstallation Create()
  {
    if (_version is null) throw new InvalidDataException("This build cannot update itself.");
    lock (_openGate) return InstallationOpener.Create(_applicationDirectory, _dataRoot, _version);
  }

  // Null only when this executable has no installation yet; any other problem throws.
  private OpenedInstallation? FindInstallation()
  {
    // Opening walks the shared parent folder with write access, so two opens at once in this process
    // (startup forwarding, the rollback check, an install) would fail each other with a sharing violation.
    lock (_openGate) return FindInstallationUnlocked();
  }

  private OpenedInstallation? FindInstallationUnlocked()
  {
    InstallationBinding? binding;
    // Release these handles before opening: the installation store walks the shared parent folder
    // with write access, which a retained read-only handle on the same ancestors would block.
    using (var directory = SafeDirectoryHandle.OpenCanonical(_applicationDirectory))
    using (var self = SupervisedProcess.CaptureCurrent())
      binding = InstallationRegistry.Open(_dataRoot, create: false)?.Resolve(
        self.Identity.ExecutablePath, directory.Identity, self.ImageIdentity);
    return binding is null ? null : InstallationOpener.Open(binding.ManagedRootPath, canCreate: true);
  }

  // For forwarding and the rollback check, where any problem simply means "no installation".
  private OpenedInstallation? TryOpen()
  {
    try { return FindInstallation(); }
    catch (Exception error) when (error is IOException or InvalidDataException or
      UnauthorizedAccessException or ArgumentException or NotSupportedException or
      System.ComponentModel.Win32Exception)
    {
      return null;
    }
  }

  private static OwnedOrOriginalTarget? Current(OpenedInstallation opened) =>
    opened.State.Read<CurrentTarget>(Path.Combine(opened.RecordsPath, "current.json")).Value?.Current;

  public void Dispose()
  {
    _admission?.Dispose();
    _transport.Dispose();
    _client.Dispose();
  }
}
