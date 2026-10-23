using System.IO;
using System.Windows;
using BookSplice.Core.Settings;
using BookSplice.Gui.Diagnostics;
using BookSplice.Gui.Bootstrap;
using BookSplice.Gui.ViewModels;
using BookSplice.Gui.Views;
using BookSplice.Gui.Updates;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Windows;

namespace BookSplice.Gui;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "OnExit disposes the update composition and lease.")]
public partial class App : Application
{
  private GuiInstanceLease? _lease;
  private UpdateComposition? _updates;
  private UpdatePreferencesStore? _preferenceStore;
  private UpdatePreferences? _preferences;
  private CrashHandler _crash = null!;
  private string? _dataRoot;

  protected override async void OnStartup(StartupEventArgs e)
  {
    base.OnStartup(e);
    var selfCheck = e.Args is ["--update-self-check", ..];
    var candidate = e.Args is ["--update-candidate", ..];
    _dataRoot = TryResolveDataRoot();
    _crash = new CrashHandler(
      () => _dataRoot is null ? null : new CrashReportStore(CrashReportStore.DirectoryFor(_dataRoot)),
      new WpfCrashUi(), ActiveJobRegistry.Shared,
      candidate ? CrashLaunchMode.UpdateCandidate : CrashLaunchMode.Normal, () => PackagedIdentity.Version);
    if (!selfCheck)
    {
      DispatcherUnhandledException += (_, args) =>
      {
        args.Handled = true;
        _crash.HandleDispatcher(args.Exception);
      };
      AppDomain.CurrentDomain.UnhandledException += (_, args) => _crash.HandleAppDomain(args.ExceptionObject);
    }
    if (e.Args.Length != 0 && e.Args[0].StartsWith("--update-", StringComparison.Ordinal))
    {
      try
      {
        if (e.Args is ["--update-self-check", var pipeName, var expectedVersion])
        {
          var isolatedRoot = Environment.GetEnvironmentVariable("BOOKSPLICE_LOCAL_APP_DATA") ?? "";
          if (!System.IO.Path.IsPathFullyQualified(isolatedRoot)) throw new InvalidDataException("Isolated data root is missing.");
          Shutdown(await UpdateSelfCheck.RunAsync(pipeName, expectedVersion, isolatedRoot,
            CancellationToken.None));
          return;
        }
        if (e.Args is ["--update-candidate", var encoded])
        {
          var request = CandidateStartupRequest.Parse(encoded, PackagedIdentity.Matches);
          _dataRoot = request.DataRoot;
          var active = await RestrictedStartup.RunCandidateAsync(request,
            window => { MainWindow = window; window.Show(); },
            lease => _lease = lease, CancellationToken.None);
          _crash.MarkUpdateCommitted();
          CreateUpdates(request.DataRoot);
          AttachUpdates(active);
          MainWindow = active;
          ShutdownMode = ShutdownMode.OnMainWindowClose;
          return;
        }
      }
      catch (Exception exception)
      {
        if (!selfCheck) _crash.HandleStartup(exception);
        Shutdown(1);
        return;
      }
      Shutdown(1);
      return;
    }
    try
    {
      var root = LocalRoot();
      var dataRoot = System.IO.Path.Combine(System.IO.Path.GetFullPath(root), "BookSplice");
      CreateUpdates(dataRoot);
      if (_updates!.TryForwardDirectLaunch()) { Shutdown(); return; }
      _lease = GuiStartup.AcquireLease(dataRoot, message =>
        MessageBox.Show(message, "BookSplice", MessageBoxButton.OK, MessageBoxImage.Information));
      if (_lease is null) { Shutdown(); return; }
      var host = new AppHost(root);
      var store = host.Settings;
      var startup = await new StartupService(store, profiles: host.Profiles).LoadAsync();
      if (startup.IsBlocked)
      {
        MessageBox.Show(startup.Message, "BookSplice settings", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
        return;
      }
      var settings = startup.Settings!;
      if (startup.RequiresSetup)
      {
        if (!string.IsNullOrEmpty(startup.Message)) MessageBox.Show(startup.Message, "BookSplice", MessageBoxButton.OK, MessageBoxImage.Information);
        var model = new FirstLaunchViewModel(new FirstLaunchService(store, profiles: host.Profiles), settings, host.Profiles);
        if (new FirstLaunchWindow(model).ShowDialog() != true) { Shutdown(); return; }
        settings = model.SavedSettings!;
      }
      var mediaTools = Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR");
      if (string.IsNullOrWhiteSpace(mediaTools)) mediaTools = System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");
      var services = await Task.Run(() => host.CreateServices(mediaTools));
      var window = new MainWindow(store, settings, services);
      AttachUpdates(window);
      MainWindow = window;
      ShutdownMode = ShutdownMode.OnMainWindowClose;
      MainWindow.Show();
    }
    catch (Exception exception)
    {
      _crash.HandleStartup(exception);
      MessageBox.Show($"BookSplice could not start. {exception.Message}", "BookSplice", MessageBoxButton.OK, MessageBoxImage.Error);
      Shutdown(1);
    }
  }

  private static string LocalRoot()
  {
    var root = Environment.GetEnvironmentVariable("BOOKSPLICE_LOCAL_APP_DATA");
    return string.IsNullOrWhiteSpace(root) ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : root;
  }

  private static string? TryResolveDataRoot()
  {
    try { return System.IO.Path.Combine(System.IO.Path.GetFullPath(LocalRoot()), "BookSplice"); }
    catch { return null; }
  }

  private void CreateUpdates(string dataRoot)
  {
    _preferenceStore = new UpdatePreferencesStore(dataRoot);
    _preferences = _preferenceStore.Load();
    _updates = new UpdateComposition(dataRoot, AppContext.BaseDirectory, PackagedIdentity.Version,
      new MemoryCheckState { LastSuccess = _preferences.LastSuccess });
  }

  private void AttachUpdates(MainWindow window)
  {
    if (PackagedIdentity.Version is { } version)
      window.UseUpdates(_updates!, _preferenceStore!, _preferences!, version,
        System.IO.Path.GetFullPath(AppContext.BaseDirectory));
  }

  protected override void OnExit(ExitEventArgs e)
  {
    _updates?.Dispose();
    _lease?.Dispose();
    base.OnExit(e);
  }
}
