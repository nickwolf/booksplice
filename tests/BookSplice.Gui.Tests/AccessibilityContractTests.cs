using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BookSplice.Core.Analysis;
using BookSplice.Core.Execution;
using BookSplice.Core.Settings;
using BookSplice.Gui.Updates;
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
  public void ImportChoiceDialogListsTheFilesAndNamesItsButtons()
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        IReadOnlyList<string> files = [@"C:\book\Disc1\01.mp3", @"C:\book\Disc2\01.mp3"];
        var window = new ImportChoiceWindow(files);
        window.Measure(new Size(window.MinWidth, window.MinHeight));
        window.Arrange(new Rect(0, 0, window.MinWidth, window.MinHeight));
        window.UpdateLayout();
        Assert.Equal(files, Assert.Single(Descendants<ListBox>(window)).ItemsSource);
        Assert.Equal(["One audiobook", "Separate books", "Cancel"], Descendants<Button>(window).Select(button => new ButtonAutomationPeer(button).GetName()));
        Assert.Equal(FileImportChoice.Cancel, window.Choice);
        window.Close();
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
          window.UseUpdates(new IdleUpdateBackend(), new UpdatePreferencesStore(root), new UpdatePreferences(AutomaticChecksEnabled: false), "0.1.0", @"C:\Apps\BookSplice");
          window.UseHistory(root);
          window.Show();
          window.UpdateLayout();
          var actions = Assert.IsType<WrapPanel>(window.FindName("PrimaryActions"));
          var buttons = LogicalTreeHelper.GetChildren(actions).OfType<Button>().Where(button => button.Visibility == Visibility.Visible).ToArray();
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
          window.UseUpdates(new IdleUpdateBackend(), new UpdatePreferencesStore(root), new UpdatePreferences(AutomaticChecksEnabled: false), "0.1.0", @"C:\Apps\BookSplice");
          window.UseHistory(root);
          window.Show();
          window.UpdateLayout();
          var image = new RenderTargetBitmap(760, 520, 96, 96, PixelFormats.Pbgra32);
          image.Render(window);
          Assert.Equal(760, image.PixelWidth);
          Assert.Equal(520, image.PixelHeight);
          Assert.All(Descendants<Button>(Assert.IsType<WrapPanel>(window.FindName("PrimaryActions"))).Where(button => button.Visibility == Visibility.Visible), button => Assert.True(button.IsVisible));
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
  [Fact]
  public void ToolbarFitsOnOneRowAtMinimumSizeWithEveryConditionalButtonVisible()
  {
    RunWithFullToolbar((window, toolbar) =>
    {
      var buttons = toolbar.Children.OfType<Button>().ToArray();
      Assert.Equal(["Add _folder...", "Add f_iles...", "Convert ready _books", "Resume _queue", "Retr_y failed", "Clear com_pleted books", "_Settings"],
        buttons.Where(button => button.Name != "UpdateAvailableButton").Select(button => button.Content));
      Assert.All(buttons, button => Assert.True(button.IsVisible, $"{button.Content} is hidden"));
      var primary = buttons.Single(button => button.Name == "ConvertReadyButton");
      var primaryTop = primary.TranslatePoint(new Point(), window).Y;
      var primaryBottom = primaryTop + primary.ActualHeight;
      Assert.All(buttons, button =>
      {
        var location = button.TranslatePoint(new Point(), window);
        Assert.True(location.X >= 0 && location.X + button.ActualWidth <= window.ActualWidth + 1, $"{button.Content} is cut off");
        Assert.True(location.Y < primaryBottom && location.Y + button.ActualHeight > primaryTop, $"{button.Content} wrapped to another row");
      });
    });
  }

  [Fact]
  public void ToolbarAccessKeysAreUniqueRegisteredAndNamed()
  {
    RunWithFullToolbar((window, toolbar) =>
    {
      var buttons = toolbar.Children.OfType<Button>().ToArray();
      var keys = buttons.Select(button => AccessKey(Assert.IsType<string>(button.Content))).ToArray();
      Assert.Equal(keys.Length, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
      Assert.All(buttons.Zip(keys), pair =>
      {
        Assert.True(AccessKeyManager.IsKeyRegistered(null, pair.Second), $"{pair.First.Content} has no registered access key");
        Assert.False(string.IsNullOrWhiteSpace(new ButtonAutomationPeer(pair.First).GetName()));
        Assert.Equal(pair.Second, new ButtonAutomationPeer(pair.First).GetAccessKey(), ignoreCase: true);
      });
      Assert.All(buttons.OfType<BookSplice.Gui.Controls.IconButton>(), icon => Assert.False(string.IsNullOrWhiteSpace(icon.ToolTip as string)));
    });
  }

  [Fact]
  public void ConditionalToolbarButtonsAndTheDropHintFollowTheQueue()
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        var root = Directory.CreateTempSubdirectory("booksplice-toolbar-").FullName;
        try
        {
          var model = new MainWindowViewModel(new QueueTests.Analyzer(), new IdleConverter(), AppSettings.Defaults);
          var window = new MainWindow(new JsonSettingsStore(root), AppSettings.Defaults) { DataContext = model, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
          window.Show();
          window.UpdateLayout();
          var hint = (UIElement)window.FindName("DropHint");
          var conditional = ConditionalButtons.Select(name => (UIElement)window.FindName(name)).ToArray();
          Assert.True(hint.IsVisible);
          Assert.All(conditional, button => Assert.False(button.IsVisible, $"{((FrameworkElement)button).Name} visible, pause={model.CanPauseQueue} retry={model.CanRetryFailed} clear={model.CanClearCompleted} items={model.Items.Count}"));

          var book = AddReady(model, "book");
          Settle(window);
          Assert.False(hint.IsVisible);
          Assert.All(conditional, button => Assert.False(button.IsVisible, $"{((FrameworkElement)button).Name} visible, pause={model.CanPauseQueue} retry={model.CanRetryFailed} clear={model.CanClearCompleted} items={model.Items.Count}"));

          book.Status = "Complete";
          Settle(window);
          Assert.True(conditional[2].IsVisible);
          Assert.False(conditional[0].IsVisible || conditional[1].IsVisible);

          model.ClearCompleted();
          Settle(window);
          Assert.True(hint.IsVisible);
          Assert.All(conditional, button => Assert.False(button.IsVisible, $"{((FrameworkElement)button).Name} visible, pause={model.CanPauseQueue} retry={model.CanRetryFailed} clear={model.CanClearCompleted} items={model.Items.Count}"));
          window.Close();
        }
        finally { Directory.Delete(root, true); }
      }
      catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
    Assert.Null(failure);
  }

  // Binding updates queue behind the dispatcher, so let them run before reading the layout.
  private static void Settle(Window window)
  {
    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    window.UpdateLayout();
  }

  private static readonly string[] ConditionalButtons = ["PauseQueueButton", "RetryFailedButton", "ClearCompletedButton"];

  private static string AccessKey(string content) => content[(content.IndexOf('_', StringComparison.Ordinal) + 1)..][..1];

  private static BookQueueItemViewModel AddReady(MainWindowViewModel model, string name)
  {
    model.AddAsync(Path.GetFullPath(name)).GetAwaiter().GetResult();
    var item = model.Items[^1];
    model.ResolveOrderAsync(item, BookSplice.Core.Ordering.OrderCandidateId.NaturalPath).GetAwaiter().GetResult();
    return item;
  }

  // A window whose queue makes Pause (shown as Resume), Retry, Clear and the update button all visible, at the minimum size.
  private static void RunWithFullToolbar(Action<MainWindow, WrapPanel> check)
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        var root = Directory.CreateTempSubdirectory("booksplice-toolbar-").FullName;
        try
        {
          var model = new MainWindowViewModel(new QueueTests.Analyzer(), new IdleConverter(), AppSettings.Defaults);
          AddReady(model, "failed").Outcome = BookSplice.Gui.Notifications.BookOutcome.Failure;
          AddReady(model, "done").Status = "Complete";
          model.PauseQueue();
          var window = new MainWindow(new JsonSettingsStore(root), AppSettings.Defaults)
          {
            DataContext = model,
            Width = 760,
            Height = 520,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000
          };
          var update = (Button)window.FindName("UpdateAvailableButton");
          update.Content = "BookSplice 10.20.30 a_vailable";
          update.Visibility = Visibility.Visible;
          window.Show();
          window.UpdateLayout();
          check(window, Assert.IsType<WrapPanel>(window.FindName("PrimaryActions")));
          window.Close();
        }
        finally { Directory.Delete(root, true); }
      }
      catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
    Assert.Null(failure);
  }

  private sealed class IdleConverter : IConversionService
  {
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null) => throw new NotSupportedException();
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
