using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using BookSplice.Core.Settings;
using BookSplice.Gui.Bootstrap;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Gui.Updates;

public sealed record CandidateStartupRequest(string PipeName, Guid TransactionId, string Nonce,
  ProcessIdentity Coordinator, FileIdentity CoordinatorImage, string CoordinatorSha256,
  ProcessIdentity OldGui, FileIdentity OldGuiImage, string OldGuiSha256,
  string DataRoot, FileIdentity DataRootIdentity, string ExpectedSettingsSha256, string Version)
{
  private static readonly JsonSerializerOptions StrictJson = new()
  {
    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
  };

  public static CandidateStartupRequest Parse(string encoded)
  {
    if (encoded.Length is 0 or > 8192) throw new InvalidDataException("Candidate context size is invalid.");
    var bytes = Convert.FromBase64String(encoded);
    if (bytes.Length > 6144) throw new InvalidDataException("Candidate context size is invalid.");
    _ = new UTF8Encoding(false, true).GetString(bytes);
    using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
    if (document.RootElement.ValueKind != JsonValueKind.Object ||
        document.RootElement.EnumerateObject().Count() != 13 ||
        document.RootElement.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 13)
      throw new InvalidDataException("Candidate context shape is invalid.");
    var context = document.RootElement.Deserialize<CandidateStartupRequest>(StrictJson) ??
      throw new InvalidDataException("Candidate context is missing.");
    if (context.TransactionId == Guid.Empty ||
        !context.PipeName.StartsWith("booksplice-update-", StringComparison.Ordinal) ||
        context.PipeName.Length != 66 || !context.PipeName[18..].All(Uri.IsHexDigit) ||
        !string.Equals(Path.GetFileName(context.Coordinator.ExecutablePath),
          "BookSplice.Launcher.exe", StringComparison.OrdinalIgnoreCase) ||
        !Path.IsPathFullyQualified(context.DataRoot) ||
        Path.GetFullPath(context.DataRoot) != context.DataRoot ||
        !string.Equals(Path.GetFileName(context.DataRoot), "BookSplice", StringComparison.OrdinalIgnoreCase) ||
        !context.DataRootIdentity.IsValid ||
        context.CoordinatorImage.IsValid is false || context.OldGuiImage.IsValid is false ||
        !ValidHash(context.CoordinatorSha256) || !ValidHash(context.OldGuiSha256) ||
        context.ExpectedSettingsSha256 != "absent" && !ValidHash(context.ExpectedSettingsSha256) ||
        context.Nonce.Length != 44 || Convert.FromBase64String(context.Nonce).Length != 32 ||
        !PackagedIdentity.Matches(context.Version))
      throw new InvalidDataException("Candidate context identity is invalid.");
    return context;
  }

  private static bool ValidHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

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
    using var oldGui = SupervisedProcess.CaptureExpected(request.OldGui,
      request.OldGuiImage, request.OldGuiSha256);
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
      var oldExited = await session.ReceiveAsync(deadline.Token);
      if (oldExited.Kind != UpdateMessageKind.OldExited)
        throw new IOException("The old GUI exit was not acknowledged.");
      using (var exitWait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
      {
        var observedExit = oldGui.WaitForExitAsync(exitWait.Token);
        var coordinatorLoss = session.ReceiveAsync(exitWait.Token);
        if (await Task.WhenAny(observedExit, coordinatorLoss) == coordinatorLoss)
        {
          exitWait.Cancel();
          try { await observedExit; }
          catch (OperationCanceledException) { }
          await coordinatorLoss;
          throw new IOException("The coordinator sent an unexpected message after old exit.");
        }
        exitWait.Cancel();
        try { await coordinatorLoss; }
        catch (OperationCanceledException) { }
        await observedExit;
      }
      await snapshot.VerifyUnchangedAsync(settingsPath, deadline.Token);
      VerifySnapshot(snapshot, request.ExpectedSettingsSha256);
      ValidateDataRoot(request.DataRoot, request.DataRootIdentity);
      if (!restricted.IsVisible) throw new IOException("The restricted window closed.");
      settingsStore.Enable();
      retainLease(lease);
      retained = true;
      restricted.IsEnabled = true;
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
