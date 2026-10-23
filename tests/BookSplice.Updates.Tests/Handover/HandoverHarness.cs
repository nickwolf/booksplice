using System.Diagnostics;
using System.Globalization;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Security;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Tests.Storage;
using BookSplice.Updates.Tests.Windows;
using BookSplice.Updates.Transport;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Handover;

internal sealed class HandoverHarness : IDisposable
{
  private readonly List<Process> _spawned = [];

  public HandoverHarness(bool installLauncher = true)
  {
    Fixture = new PackageStagerTests.Fixture(runnableSelfCheck: true);
    var root = Layout.OriginalApplicationPath;
    OriginalPackageFixture.Write(root, "0.1.0");
    var helper = ProcessSupervisorTests.HelperPath();
    var sourceBase = Path.Combine(Path.GetDirectoryName(helper)!, "BookSplice.LeaseHost");
    File.Copy(helper, Path.Combine(root, "BookSplice.Gui.exe"), true);
    foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
      File.Copy(sourceBase + extension, Path.Combine(root, "BookSplice.LeaseHost" + extension), true);
    File.Copy(Path.Combine(Path.GetDirectoryName(helper)!, "BookSplice.Updates.dll"),
      Path.Combine(root, "BookSplice.Updates.dll"), true);
    Installation = InstallationRecord.FromLayout(Layout, "0.1.0");
    OldTarget = new OwnedOrOriginalTarget(TargetKind.Original,
      Installation.OriginalExecutablePath, Installation.OriginalIdentity,
      Installation.OriginalExecutableIdentity, Installation.OriginalPackage,
      Installation.OriginalVersion, Installation.OriginalProtocolVersion, null);
    (Stager, Journal, State, _) = Fixture.CreateDurableStager(beginJournal: false,
      writeFault: point => WriteFault?.Invoke(point));
    State.Write(InstallationPath, Installation);
    State.Write(MappingPath, new CurrentTarget(1, Layout.InstallationId, OldTarget, null));
    File.WriteAllText(SettingsPath, "{\"schemaVersion\":1,\"unknown\":\"kept\"}");
    if (installLauncher)
      StableLauncherBootstrap.Ensure(Layout, JournalSnapshot.Read(State, Layout.Records.Path),
        State, InstallationPath);
    Targets = new CurrentTargetStore(State, Journal, Layout);
    Self = SupervisedProcess.CaptureCurrent();
    Helper = VerifiedExecutable.PinFile(helper);
    MarkerPath = Path.Combine(Path.GetDirectoryName(Layout.DataRoot)!, "candidate-marker.txt");
  }

  public PackageStagerTests.Fixture Fixture { get; }
  public InstallationLayout Layout => Fixture.Layout;
  public InstallationRecord Installation { get; }
  public OwnedOrOriginalTarget OldTarget { get; }
  public PackageStager Stager { get; }
  public UpdateJournal Journal { get; }
  public AtomicStateStore State { get; }
  public CurrentTargetStore Targets { get; }
  public SupervisedProcess Self { get; }
  public VerifiedExecutable Helper { get; }
  public string MarkerPath { get; }
  public Action<StateWritePoint>? WriteFault { get; set; }
  public TrackingLeaseStore Admission { get; } = new();
  public List<LaunchDecision> FallbackLaunches { get; } = [];
  public List<ProcessIdentity> Candidates { get; } = [];
  public bool CandidateExited(int index) =>
    _spawned.SingleOrDefault(x => x.Id == Candidates[index].Pid) is not { } process ||
    process.HasExited;
  public string SettingsPath => Path.Combine(Layout.DataRoot, "settings.json");
  public string MappingPath => Path.Combine(Layout.Records.Path, "current.json");
  public string InstallationPath => Path.Combine(Layout.Records.Path, "installation.json");
  public CurrentTarget? Mapping => State.Read<CurrentTarget>(MappingPath).Value;
  public UpdateTransaction? Transaction => Journal.Read().Value;

  public ConfirmedUpdate Update() => new(Fixture.Release,
    ReleaseSelector.Select([Fixture.Release]).Identity!, Fixture.Manifest.ManifestSha256!,
    OldTarget, Guid.NewGuid());

  public UpdatePreparation Preparation(IActivityLeaseStore? activity = null,
    ISignedReleaseReader? reader = null) => new(State,
      reader ?? new FixedManifestReader(Fixture.Manifest), Stager, Journal, Targets,
      activity ?? Admission, Fixture.Store);

  public async Task<Guid> PrepareUpdateAsync()
  {
    var prepared = await Preparation().PrepareUpdateAsync(Update(), Layout,
      CancellationToken.None);
    Assert.True(prepared.Outcome.Kind == UpdateOutcomeKind.Prepared, prepared.Outcome.Reason);
    prepared.Admission!.Dispose();
    return prepared.TransactionId;
  }

  public async Task<Guid> PrepareRollbackAsync()
  {
    var prepared = await Preparation().PrepareRollbackAsync(Layout, CancellationToken.None);
    Assert.True(prepared.Outcome.Kind == UpdateOutcomeKind.Prepared, prepared.Outcome.Reason);
    prepared.Admission!.Dispose();
    return prepared.TransactionId;
  }

  public LaunchHandover Handover(Func<CandidateStartupRequest, SupervisedProcess>? candidate = null,
    IActivityLeaseStore? activity = null, Func<SupervisedProcess, Task>? stop = null,
    Action? afterReady = null, TimeSpan? readiness = null, TimeSpan? oldExit = null) =>
    new(State, Journal, Targets, activity ?? Admission, Fixture.Store, Self)
    {
      CandidateLauncher = (_, request) => (candidate ?? (r => Candidate(r)))(request),
      LaunchMapped = layout =>
      {
        var decision = LaunchResolver.Resolve(layout,
          JournalSnapshot.Read(State, layout.Records.Path));
        FallbackLaunches.Add(decision);
        return decision;
      },
      CandidateStop = stop,
      AfterReady = afterReady,
      ReadinessTimeout = readiness ?? TimeSpan.FromSeconds(20),
      OldExitTimeout = oldExit ?? TimeSpan.FromSeconds(20),
      AdmissionTimeout = TimeSpan.FromMilliseconds(300)
    };

  public Task<UpdateOutcome> RunAsync(LaunchHandover handover, Guid id, ProcessIdentity old) =>
    handover.RunAsync(id, old.Pid, old.StartTimeUtcTicks, Layout, CancellationToken.None);

  public SupervisedProcess Candidate(CandidateStartupRequest request, string mode = "ready",
    int delayMilliseconds = 0)
  {
    string[] args = mode switch
    {
      "hold" => ["hold"],
      "exit" => ["write-data-env", Path.Combine(Path.GetDirectoryName(Layout.DataRoot)!,
        "exit-" + Guid.NewGuid().ToString("N"))],
      _ =>
      [
        "pipe-ready", request.PipeName, request.TransactionId.ToString("D"),
        Self.Identity.Pid.ToString(CultureInfo.InvariantCulture),
        Self.Identity.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
        Self.Identity.ExecutablePath,
        Convert.ToHexString(Convert.FromBase64String(request.Nonce)),
        Self.ImageIdentity.VolumeSerial.ToString(CultureInfo.InvariantCulture),
        Self.ImageIdentity.FileIdHex, Self.ImageSha256,
        delayMilliseconds.ToString(CultureInfo.InvariantCulture), MarkerPath
      ]
    };
    var child = ProcessSupervisor.Start(Helper, args, new Dictionary<string, string>());
    Track(child);
    Candidates.Add(child.Identity);
    return child;
  }

  public SupervisedProcess StartOld()
  {
    SupervisedProcess old;
    using (var executable = VerifiedExecutable.PinFile(OldTarget.ExecutablePath))
      old = ProcessSupervisor.Start(executable, ["hold"], new Dictionary<string, string>());
    Track(old);
    return old;
  }

  public async Task<ProcessIdentity> ExitedOldAsync()
  {
    using var old = StartOld();
    await old.StopAsync();
    return old.Identity;
  }

  public async Task WaitForMarkerAsync()
  {
    for (var attempt = 0; attempt < 100 && !File.Exists(MarkerPath); attempt++)
      await Task.Delay(100);
    Assert.True(File.Exists(MarkerPath), "The candidate did not write after Commit.");
  }

  public async Task StopCandidatesAsync()
  {
    foreach (var process in _spawned.Where(x => Candidates.Any(c => c.Pid == x.Id) && !x.HasExited))
    {
      process.Kill();
      await process.WaitForExitAsync();
    }
  }

  private void Track(SupervisedProcess process)
  {
    try { _spawned.Add(Process.GetProcessById(process.Identity.Pid)); }
    catch (ArgumentException) { }
  }

  public void Dispose()
  {
    foreach (var process in _spawned)
    {
      try
      {
        if (!process.HasExited) process.Kill();
        process.WaitForExit(10_000);
      }
      catch (InvalidOperationException) { }
      process.Dispose();
    }
    Helper.Dispose();
    Self.Dispose();
    for (var attempt = 0; ; attempt++)
    {
      try { Fixture.Dispose(); break; }
      catch (Exception error) when (attempt < 40 &&
        error is IOException or UnauthorizedAccessException)
      { Thread.Sleep(100); }
    }
  }
}

internal sealed class FixedManifestReader(VerifiedManifest manifest,
  Action? beforeReturn = null) : ISignedReleaseReader
{
  public Task<VerifiedManifest> ReadAsync(ReleaseCandidate release, int settingsSchema,
    CancellationToken cancellationToken)
  {
    beforeReturn?.Invoke();
    return Task.FromResult(manifest);
  }
}

internal sealed class TrackingLeaseStore(Func<bool>? refuse = null) : IActivityLeaseStore
{
  public int Entered { get; private set; }
  public int Released { get; private set; }
  public IDisposable? TryEnterJob() => null;
  public IDisposable? TryEnterUpdate()
  {
    if (refuse?.Invoke() == true) return null;
    Entered++;
    return new Release(() => Released++);
  }
  private sealed class Release(Action release) : IDisposable
  {
    public void Dispose() => release();
  }
}
