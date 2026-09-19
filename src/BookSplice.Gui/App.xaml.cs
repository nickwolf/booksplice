using System.Windows;
using BookSplice.Core.Settings;
using BookSplice.Gui.Bootstrap;
using BookSplice.Gui.ViewModels;
using BookSplice.Gui.Views;

namespace BookSplice.Gui;

public partial class App : Application
{
  protected override async void OnStartup(StartupEventArgs e)
  {
    base.OnStartup(e);
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
}
