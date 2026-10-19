using System.IO;
using System.Windows;
using BookSplice.Core.Settings;
using BookSplice.Gui.Bootstrap;
using BookSplice.Gui.ViewModels;
using BookSplice.Gui.Views;
using BookSplice.Gui.Updates;
using BookSplice.Updates.Windows;

namespace BookSplice.Gui;

public partial class App : Application
{
  private GuiInstanceLease? _candidateLease;

  protected override async void OnStartup(StartupEventArgs e)
  {
    base.OnStartup(e);
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
          var request = CandidateStartupRequest.Parse(encoded);
          var active = await RestrictedStartup.RunCandidateAsync(request,
            window => { MainWindow = window; window.Show(); },
            lease => _candidateLease = lease, CancellationToken.None);
          MainWindow = active;
          ShutdownMode = ShutdownMode.OnMainWindowClose;
          return;
        }
      }
      catch { Shutdown(1); return; }
      Shutdown(1);
      return;
    }
    try
    {
      var root = Environment.GetEnvironmentVariable("BOOKSPLICE_LOCAL_APP_DATA");
      if (string.IsNullOrWhiteSpace(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      var host = new AppHost(root);
      var store = host.Settings;
      var startup = await new StartupService(store).LoadAsync();
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
        var model = new FirstLaunchViewModel(new FirstLaunchService(store), settings);
        if (new FirstLaunchWindow(model).ShowDialog() != true) { Shutdown(); return; }
        settings = model.SavedSettings!;
      }
      var mediaTools = Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR");
      if (string.IsNullOrWhiteSpace(mediaTools)) mediaTools = System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");
      var services = await Task.Run(() => host.CreateServices(mediaTools));
      MainWindow = new MainWindow(store, settings, services);
      ShutdownMode = ShutdownMode.OnMainWindowClose;
      MainWindow.Show();
    }
    catch (Exception exception)
    {
      MessageBox.Show($"BookSplice could not start. {exception.Message}", "BookSplice", MessageBoxButton.OK, MessageBoxImage.Error);
      Shutdown(1);
    }
  }

  protected override void OnExit(ExitEventArgs e)
  {
    _candidateLease?.Dispose();
    base.OnExit(e);
  }
}
