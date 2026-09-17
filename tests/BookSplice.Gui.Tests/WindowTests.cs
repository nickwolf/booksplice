using System.Windows;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;
using BookSplice.Gui.Views;

namespace BookSplice.Gui.Tests;

public sealed class WindowTests
{
  [Fact]
  public void SetupAndMainWindowsLoadOnWindowsDispatcher()
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        var store = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var setup = new FirstLaunchWindow(new FirstLaunchViewModel(new FirstLaunchService(store), AppSettings.Defaults));
        setup.Measure(new Size(600, 450));
        setup.Arrange(new Rect(0, 0, 600, 450));
        Assert.NotNull(setup.Content);
        Assert.True(setup.MinWidth <= 600);
        setup.Close();
        var main = new MainWindow(store, AppSettings.Defaults);
        main.Measure(new Size(900, 560));
        Assert.NotNull(main.Content);
        main.Close();
      }
      catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
    Assert.Null(failure);
  }
}
