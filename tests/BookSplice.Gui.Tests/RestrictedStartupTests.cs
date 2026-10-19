using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using BookSplice.Gui;
using BookSplice.Gui.Updates;
using BookSplice.Core.Settings;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Windows;

namespace BookSplice.Gui.Tests;

public sealed class RestrictedStartupTests
{
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task HeldGuiLeasePermitsSettingsSave(bool existing)
  {
    var parent = Path.Combine(Path.GetTempPath(), "booksplice-lease-save-" + Guid.NewGuid().ToString("N"));
    var root = Path.Combine(parent, "BookSplice");
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "settings.json");
    try
    {
      var store = new JsonSettingsStore(parent);
      var valid = AppSettings.Defaults with { OutputDirectory = parent };
      if (existing) await store.SaveAsync(valid);
      using var lease = GuiInstanceLease.TryAcquire(root);
      Assert.NotNull(lease);
      var activity = new ActivityLeaseStore(root);
      using var job = activity.TryEnterJob();
      Assert.NotNull(job);
      Assert.ThrowsAny<Exception>(() => Directory.Move(root, root + "-moved"));
      Assert.ThrowsAny<Exception>(() => File.Move(Path.Combine(root, "gui-instance.lock"),
        Path.Combine(root, "gui-instance.old")));
      Assert.ThrowsAny<Exception>(() => File.Move(Path.Combine(root, "activity.lock"),
        Path.Combine(root, "activity.old")));
      await store.SaveAsync(valid);
      Assert.True(File.Exists(path));
    }
    finally { Directory.Delete(parent, true); }
  }

  [Fact]
  public void PinnedBundledMediaToolsExposeRequiredCapabilities()
  {
    PrepareVerifiedTools();
    var tools = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");
    UpdateSelfCheck.ProbeTool(Path.Combine(tools, "ffmpeg.exe"), "ffmpegSha256",
      "ffmpeg", "-encoders", @"(?m)^\s*A[.A-Z]{5}\s+aac\s");
    UpdateSelfCheck.ProbeTool(Path.Combine(tools, "ffprobe.exe"), "ffprobeSha256",
      "ffprobe", "-formats", @"(?m)^\s*D\s+mov,mp4,m4a");
  }

  [Fact]
  public void RejectsMismatchedAndReplacedDataRoots()
  {
    var parent = Path.Combine(Path.GetTempPath(), "booksplice-candidate-root-" + Guid.NewGuid().ToString("N"));
    var root = Path.Combine(parent, "BookSplice");
    Directory.CreateDirectory(root);
    try
    {
      FileIdentity identity;
      using (var first = SafeDirectoryHandle.OpenCanonical(root)) identity = first.Identity;
      Assert.Throws<InvalidDataException>(() =>
        RestrictedStartup.ValidateDataRoot(parent, identity));
      RestrictedStartup.ValidateDataRoot(root, identity);
      Directory.Move(root, root + "-old");
      Directory.CreateDirectory(root);
      Assert.Throws<InvalidDataException>(() =>
        RestrictedStartup.ValidateDataRoot(root, identity));
    }
    finally { Directory.Delete(parent, true); }
  }

  [Theory]
  [InlineData(0)]
  [InlineData(1)]
  [InlineData(2)]
  public void AuthenticatedCandidateKeepsSameWindowRestrictedUntilOldExit(int disconnectPhase)
  {
    OnStaAsync(async () =>
    {
      PrepareVerifiedTools();
      var parent = Path.Combine(Path.GetTempPath(), "booksplice-candidate-" + Guid.NewGuid().ToString("N"));
      var root = Path.Combine(parent, "BookSplice");
      Directory.CreateDirectory(root);
      var rootHandle = SafeDirectoryHandle.OpenCanonical(root);
      using var current = SupervisedProcess.CaptureCurrent();
      using var capability = VerifiedExecutable.PinFile(HelperPath());
      using var old = ProcessSupervisor.Start(capability, ["hold"], new Dictionary<string, string>());
      var pipeName = UpdatePipe.NewPipeName();
      var nonce = UpdatePipe.NewNonce();
      var transaction = Guid.NewGuid();
      using var server = UpdatePipe.CreateServer(pipeName);
      var request = new CandidateStartupRequest(pipeName, transaction,
        Convert.ToBase64String(nonce), current.Identity, current.ImageIdentity, current.ImageSha256,
        old.Identity, old.ImageIdentity, old.ImageSha256, root, rootHandle.Identity, "absent", "0.2.0");
      MainWindow? shown = null;
      GuiInstanceLease? retained = null;
      var shows = 0;
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
      var accept = UpdatePipe.AcceptAsync(server, current, transaction, nonce, current, timeout.Token);
      var candidate = RestrictedStartup.RunCandidateAsync(request,
        window => { shown = window; shows++; window.Show(); },
        lease => retained = lease, timeout.Token);
      try
      {
        if (await Task.WhenAny(accept, candidate) == candidate) await candidate;
        using var session = await accept;
        var readyTask = session.ReceiveAsync(timeout.Token);
        if (await Task.WhenAny(readyTask, candidate) == candidate)
          await candidate;
        var ready = await readyTask;
        Assert.Equal(UpdateMessageKind.Ready, ready.Kind);
        Assert.NotNull(shown);
        Assert.True(shown.IsVisible);
        Assert.False(shown.IsEnabled);
        Assert.False(File.Exists(Path.Combine(root, "settings.json")));
        var guardedStore = (ISettingsStore)typeof(MainWindow).GetField("_store",
          System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
          .GetValue(shown)!;
        var validSettings = AppSettings.Defaults with { OutputDirectory = parent };
        await Assert.ThrowsAsync<InvalidOperationException>(() => guardedStore.SaveAsync(validSettings));
        if (disconnectPhase == 1)
        {
          session.Dispose();
          await Assert.ThrowsAnyAsync<Exception>(async () => await candidate);
          Assert.False(shown.IsVisible);
          Assert.False(File.Exists(Path.Combine(root, "settings.json")));
          return;
        }
        await session.SendAsync(UpdateMessageKind.Commit, timeout.Token);
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.False(shown.IsEnabled);
        if (disconnectPhase == 2)
        {
          await session.SendAsync(UpdateMessageKind.OldExited, timeout.Token);
          await Dispatcher.Yield(DispatcherPriority.Background);
          Assert.False(shown.IsEnabled);
          session.Dispose();
          await Assert.ThrowsAnyAsync<Exception>(async () => await candidate);
          Assert.False(shown.IsVisible);
          Assert.False(File.Exists(Path.Combine(root, "settings.json")));
          return;
        }
        await old.StopAsync(timeout.Token);
        await session.SendAsync(UpdateMessageKind.OldExited, timeout.Token);
        var active = await candidate;
        Assert.Same(shown, active);
        Assert.Equal(1, shows);
        Assert.True(active.IsEnabled);
        Assert.False(File.Exists(Path.Combine(root, "settings.json")));
        rootHandle.Dispose();
        await guardedStore.SaveAsync(validSettings);
        Assert.True(File.Exists(Path.Combine(root, "settings.json")));
        active.Close();
      }
      finally
      {
        retained?.Dispose();
        if (shown is { IsVisible: true }) shown.Close();
        if (!old.HasExited) await old.StopAsync();
        rootHandle.Dispose();
        Directory.Delete(parent, true);
      }
    });
  }

  private static void PrepareVerifiedTools()
  {
    var repository = Directory.GetParent(Directory.GetParent(SolutionRoot())!.FullName)!.FullName;
    var root = Path.Combine(repository, "releases", "v0.1.1-public", "app");
    var source = Path.Combine(root, "sources", "manifest.json");
    Assert.True(File.Exists(source), "The pinned public media tool fixture is required.");
    using var manifest = JsonDocument.Parse(File.ReadAllBytes(source));
    var manifestTarget = Path.Combine(AppContext.BaseDirectory, "sources");
    Directory.CreateDirectory(manifestTarget);
    File.Copy(source, Path.Combine(manifestTarget, "manifest.json"), true);
    var target = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");
    Directory.CreateDirectory(target);
    foreach (var (name, field) in new[] { ("ffmpeg.exe", "ffmpegSha256"),
      ("ffprobe.exe", "ffprobeSha256") })
    {
      var path = Path.Combine(root, "tools", "ffmpeg", name);
      using var file = File.OpenRead(path);
      Assert.Equal(manifest.RootElement.GetProperty(field).GetString(),
        Convert.ToHexString(SHA256.HashData(file)), ignoreCase: true);
      File.Copy(path, Path.Combine(target, name), true);
    }
  }

  private static string HelperPath() => Path.Combine(SolutionRoot(), "tests", "BookSplice.LeaseHost",
    "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
      ?? throw new DirectoryNotFoundException(), "net10.0-windows", "BookSplice.LeaseHost.exe");

  private static string SolutionRoot()
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "BookSplice.slnx")))
      current = current.Parent;
    return current?.FullName ?? throw new DirectoryNotFoundException();
  }

  private static void OnStaAsync(Func<Task> action)
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var task = action();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false),
          TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
        dispatcher.InvokeShutdown();
      }
      catch (Exception error) { failure = error; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
  }
}
