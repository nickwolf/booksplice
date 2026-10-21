using System.IO;
using System.Windows;
using BookSplice.Core.Settings;
using BookSplice.Gui.Bootstrap;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Gui.Updates;

public static class RestrictedStartup
{
  public static async Task<MainWindow> RunCandidateAsync(CandidateStartupRequest request,
    Action<MainWindow> show, Action<GuiInstanceLease> retainLease, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(show);
    ArgumentNullException.ThrowIfNull(retainLease);
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(TimeSpan.FromSeconds(60));
    var token = deadline.Token;
    using var retainedDataRoot = SafeDirectoryHandle.OpenCanonical(request.DataRoot);
    ValidateDataRoot(request.DataRoot, request.DataRootIdentity);
    if (retainedDataRoot.Identity != request.DataRootIdentity)
      throw new InvalidDataException("Candidate data root differs from the retained root.");
    var settingsPath = Path.Combine(request.DataRoot, "settings.json");
    var snapshot = await SettingsSnapshot.CaptureAsync(settingsPath, token);
    VerifySnapshot(snapshot, request.ExpectedSettingsSha256);
    var loaded = JsonSettingsStore.ParseSnapshot(snapshot.Bytes);
    if (loaded.Code is not (SettingsLoadCode.Valid or SettingsLoadCode.Missing))
      throw new InvalidDataException("Candidate settings are incompatible.");
    using var coordinator = SupervisedProcess.CaptureExpected(request.Coordinator,
      request.CoordinatorImage, request.CoordinatorSha256);
    using var local = SupervisedProcess.CaptureCurrent();
    using var session = await UpdatePipe.ConnectAsync(request.PipeName, coordinator,
      request.TransactionId, Convert.FromBase64String(request.Nonce), local, token);
    var lease = GuiInstanceLease.TryAcquire(request.DataRoot) ??
      throw new IOException("The expected GUI instance lease is unavailable.");
    var retained = false;
    MainWindow? restricted = null;
    EventHandler? cancelOnClose = null;
    try
    {
      var parent = Path.GetDirectoryName(request.DataRoot)!;
      var host = new AppHost(parent);
      var tools = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");
      var services = await Task.Run(() => host.CreateServices(tools), token);
      var settingsStore = new RestrictedSettingsStore(host.Settings, loaded.Settings!);
      restricted = new MainWindow(settingsStore, loaded.Settings!, services)
      { IsEnabled = false };
      var ready = WpfReadiness.WaitAsync(restricted, restricted.Dispatcher, deadline.Token);
      cancelOnClose = (_, _) => deadline.Cancel();
      restricted.Closed += cancelOnClose;
      show(restricted);
      await ready;
      await session.SendAsync(UpdateMessageKind.Ready, deadline.Token);
      var commit = await session.ReceiveAsync(deadline.Token);
      if (commit.Kind != UpdateMessageKind.Commit) throw new IOException("Candidate commit was refused.");
      await snapshot.VerifyUnchangedAsync(settingsPath, deadline.Token);
      VerifySnapshot(snapshot, request.ExpectedSettingsSha256);
      ValidateDataRoot(request.DataRoot, request.DataRootIdentity);
      if (!restricted.IsVisible) throw new IOException("The restricted window closed.");
      settingsStore.Enable();
      retainLease(lease);
      restricted.IsEnabled = true;
      retained = true;
      return restricted;
    }
    finally
    {
      if (restricted is not null && cancelOnClose is not null)
        restricted.Closed -= cancelOnClose;
      if (!retained) lease.Dispose();
      if (!retained && restricted is { IsVisible: true }) restricted.Close();
    }
  }

  private static void VerifySnapshot(SettingsSnapshot snapshot, string expected)
  {
    if (!string.Equals(snapshot.Sha256 ?? "absent", expected, StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("Candidate settings differ from the handover snapshot.");
  }

  internal static void ValidateDataRoot(string path, FileIdentity expected)
  {
    if (!string.Equals(Path.GetFileName(path), "BookSplice", StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("Candidate data root is not a BookSplice root.");
    using var root = SafeDirectoryHandle.OpenCanonical(path);
    if (root.Identity != expected)
      throw new InvalidDataException("Candidate data root differs from the handover root.");
  }

  private sealed class RestrictedSettingsStore(ISettingsStore inner, AppSettings settings) : ISettingsStore
  {
    private bool _enabled;
    public string SettingsPath => inner.SettingsPath;
    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
      _enabled ? inner.LoadAsync(cancellationToken) :
        Task.FromResult(new SettingsLoadResult(SettingsLoadCode.Valid, settings, []));
    public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default)
    {
      if (!_enabled) throw new InvalidOperationException("The candidate is restricted during handover.");
      return inner.SaveAsync(value, cancellationToken);
    }
    public void Enable() => _enabled = true;
  }
}
