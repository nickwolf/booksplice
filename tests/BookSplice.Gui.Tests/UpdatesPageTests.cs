using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using BookSplice.Core.Settings;
using BookSplice.Gui.Updates;
using BookSplice.Gui.Views;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Policy;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class UpdatesWindowTests
{
  [Fact]
  public void WindowExposesNamedKeyboardControlsAndAPoliteStatusRegion() => DispatcherTestHost.Run(dispatcher =>
  {
    var root = Directory.CreateTempSubdirectory("booksplice-updates-window-").FullName;
    try
    {
      var model = new UpdatesViewModel(new IdleUpdateBackend(), new UpdatePreferencesStore(root), new UpdatePreferences(), "0.1.0",
        @"C:\Apps\BookSplice", new GuiOperationGate(dispatcher), () => 0, () => { });
      var window = new UpdatesWindow(model) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
      window.Measure(new Size(window.MinWidth, window.MinHeight));
      window.Arrange(new Rect(0, 0, window.MinWidth, window.MinHeight));
      window.UpdateLayout();
      var buttons = Descendants<Button>(window).ToArray();
      Assert.NotEmpty(buttons);
      Assert.All(buttons, button => Assert.False(string.IsNullOrWhiteSpace(new ButtonAutomationPeer(button).GetName())));
      Assert.All(buttons, button => Assert.True(button.Focusable && button.IsTabStop));
      var status = Assert.IsType<TextBlock>(window.FindName("Status"));
      Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
      Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(Assert.IsType<CheckBox>(window.FindName("Automatic")))));
      Assert.All(Descendants<TextBox>(window), input => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(input))));
      Assert.False(model.CanInstall);
      model.Dispose();
    }
    finally { Directory.Delete(root, true); }
    return Task.CompletedTask;
  });

  [Fact]
  public void MainWindowShowsTheUpdatesEntryPointOnlyWhenUpdatesAreAvailable() => DispatcherTestHost.Run(dispatcher =>
  {
    var root = Directory.CreateTempSubdirectory("booksplice-updates-entry-").FullName;
    try
    {
      var window = new MainWindow(new JsonSettingsStore(root), AppSettings.Defaults);
      Assert.Equal(Visibility.Collapsed, window.UpdatesButton.Visibility);
      window.UseUpdates(new IdleUpdateBackend(), new UpdatePreferencesStore(root), new UpdatePreferences(AutomaticChecksEnabled: false), "0.1.0", @"C:\Apps\BookSplice");
      Assert.Equal(Visibility.Visible, window.UpdatesButton.Visibility);
      Assert.False(string.IsNullOrWhiteSpace(new ButtonAutomationPeer(window.UpdatesButton).GetName()));
    }
    finally { Directory.Delete(root, true); }
    return Task.CompletedTask;
  });

  private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
  {
    foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
    {
      if (child is T match) yield return match;
      foreach (var descendant in Descendants<T>(child)) yield return descendant;
    }
  }
}
