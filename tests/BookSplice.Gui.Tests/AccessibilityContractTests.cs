using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;
using BookSplice.Gui.Views;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class AccessibilityContractTests
{
  [Fact]
  public void GuiManifestDeclaresPerMonitorV2DpiAwareness()
  {
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root is not null && !File.Exists(Path.Combine(root.FullName, "BookSplice.slnx"))) root = root.Parent;
    Assert.NotNull(root);
    var manifest = File.ReadAllText(Path.Combine(root.FullName, "src", "BookSplice.Gui", "app.manifest"));
    Assert.Contains("<dpiAwareness", manifest, StringComparison.Ordinal);
    Assert.Contains("PerMonitorV2,PerMonitor", manifest, StringComparison.Ordinal);
  }
  [Fact]
  public void SetupWindowExposesNamesForInteractiveControlsAtMinimumSize()
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        var root = Directory.CreateTempSubdirectory("booksplice-accessibility-").FullName;
        try
        {
          var store = new JsonSettingsStore(root);
          var window = new FirstLaunchWindow(new FirstLaunchViewModel(new FirstLaunchService(store), AppSettings.Defaults));
          window.Measure(new Size(window.MinWidth, window.MinHeight));
          window.Arrange(new Rect(0, 0, window.MinWidth, window.MinHeight));
          window.UpdateLayout();

          var buttons = Descendants<Button>(window).ToArray();
          Assert.NotEmpty(buttons);
          Assert.All(buttons, button => Assert.False(string.IsNullOrWhiteSpace(new ButtonAutomationPeer(button).GetName())));
          Assert.All(Descendants<ComboBox>(window), combo => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(combo))));
          Assert.All(Descendants<TextBox>(window), input => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(input))));
          window.Close();
        }
        finally { Directory.Delete(root, true); }
      }
      catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
    Assert.Null(failure);
  }

  [Fact]
  public void MainActionsRemainVisibleAtMinimumWindowSize()
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        var root = Directory.CreateTempSubdirectory("booksplice-layout-").FullName;
        try
        {
          var window = new MainWindow(new JsonSettingsStore(root), AppSettings.Defaults)
          {
            Width = 760,
            Height = 520,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000
          };
          Assert.True(window.MinWidth * 2 <= 1920);
          Assert.True(window.MinHeight * 2 <= 1080);
          window.Show();
          window.UpdateLayout();
          var actions = Assert.IsType<WrapPanel>(window.FindName("PrimaryActions"));
          var buttons = LogicalTreeHelper.GetChildren(actions).OfType<Button>().ToArray();
          Assert.NotEmpty(buttons);
          Assert.All(buttons, button =>
          {
            var location = button.TranslatePoint(new Point(), window);
            Assert.True(button.ActualWidth > 0);
            Assert.True(location.X >= 0 && location.X + button.ActualWidth <= window.ActualWidth + 1);
          });
          window.Close();
        }
        finally { Directory.Delete(root, true); }
      }
      catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
    Assert.Null(failure);
  }
  [Fact]
  public void MainWindowMinimumSizeFitsAFullHdWorkAreaAtTwoHundredPercentScaling()
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        var root = Directory.CreateTempSubdirectory("booksplice-dpi-").FullName;
        try
        {
          var window = new MainWindow(new JsonSettingsStore(root), AppSettings.Defaults)
          {
            Width = 760,
            Height = 520,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000
          };
          Assert.True(window.MinWidth * 2 <= 1920);
          Assert.True(window.MinHeight * 2 <= 1080);
          window.Show();
          window.UpdateLayout();
          var image = new RenderTargetBitmap(760, 520, 96, 96, PixelFormats.Pbgra32);
          image.Render(window);
          Assert.Equal(760, image.PixelWidth);
          Assert.Equal(520, image.PixelHeight);
          Assert.All(Descendants<Button>(Assert.IsType<WrapPanel>(window.FindName("PrimaryActions"))), button => Assert.True(button.IsVisible));
          window.Close();
        }
        finally { Directory.Delete(root, true); }
      }
      catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
    Assert.Null(failure);
  }
  [Fact]
  public void InteractiveButtonsExposeKeyboardAccessKeys()
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        var root = Directory.CreateTempSubdirectory("booksplice-keyboard-").FullName;
        try
        {
          var setup = new FirstLaunchWindow(new FirstLaunchViewModel(new FirstLaunchService(new JsonSettingsStore(root)), AppSettings.Defaults))
          { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
          var main = new MainWindow(new JsonSettingsStore(root), AppSettings.Defaults)
          { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
          setup.Show();
          main.Show();
          var setupButtons = Descendants<Button>(setup).ToArray();
          var mainButtons = Descendants<Button>(main).ToArray();
          Assert.All(setupButtons.Concat(mainButtons), button => Assert.Contains("_", Assert.IsType<string>(button.Content), StringComparison.Ordinal));
          Assert.All(mainButtons.Where(button => button.IsVisible), button => Assert.False(string.IsNullOrWhiteSpace(new ButtonAutomationPeer(button).GetAccessKey())));
          var tabs = Assert.Single(Descendants<TabControl>(main));
          foreach (var tab in tabs.Items.OfType<TabItem>())
          {
            tabs.SelectedItem = tab;
            main.UpdateLayout();
            Assert.All(Descendants<Button>(tab).Where(button => button.IsVisible), button => Assert.False(string.IsNullOrWhiteSpace(new ButtonAutomationPeer(button).GetAccessKey())));
          }
          setup.Close();
          main.Close();
        }
        finally { Directory.Delete(root, true); }
      }
      catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
    Assert.Null(failure);
  }
  private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
  {
    foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
    {
      if (child is T match) yield return match;
      foreach (var descendant in Descendants<T>(child)) yield return descendant;
    }
  }
}
