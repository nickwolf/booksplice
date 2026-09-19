using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;
using BookSplice.Gui.Views;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class AccessibilityContractTests
{
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
  private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
  {
    foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
    {
      if (child is T match) yield return match;
      foreach (var descendant in Descendants<T>(child)) yield return descendant;
    }
  }
}
