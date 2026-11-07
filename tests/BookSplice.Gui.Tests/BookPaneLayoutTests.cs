using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using BookSplice.Core.Analysis;
using BookSplice.Core.Covers;
using BookSplice.Core.Execution;
using BookSplice.Core.Ordering;
using BookSplice.Core.Settings;
using BookSplice.Core.Validation;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class BookPaneLayoutTests
{
  [Fact]
  public void WithNoBookSelectedThePaneShowsOnlyAHintAndNeverScrolls() => RunWindow(1120, 760, async (window, _) =>
  {
    var scroll = (ScrollViewer)window.FindName("BookPaneScroll");
    var panel = (DockPanel)window.FindName("SelectedBookPanel");
    var hint = (TextBlock)window.FindName("NoBookHint");
    Assert.Equal(Visibility.Collapsed, panel.Visibility);
    Assert.Equal(Visibility.Visible, hint.Visibility);
    Assert.Equal(0, scroll.ScrollableHeight);
    await Task.CompletedTask;
  });

  [Fact]
  public void WithABookSelectedThePaneFitsTheDefaultWindowAndTheGridsFillTheTabs() => RunWindow(1120, 760, async (window, model) =>
  {
    var item = await Select(window, model);
    var scroll = (ScrollViewer)window.FindName("BookPaneScroll");
    var panel = (DockPanel)window.FindName("SelectedBookPanel");
    Assert.Equal(Visibility.Visible, panel.Visibility);
    var status = Descendants<TextBox>(panel).Single(box => AutomationProperties.GetName(box) == "Status and diagnostics");
    Assert.Equal(Visibility.Collapsed, status.Visibility);
    item.Details = "Something to read.";
    Settle(window);
    Assert.Equal(Visibility.Visible, status.Visibility);
    item.Details = "";
    Settle(window);
    Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("NoBookHint")).Visibility);
    Assert.True(scroll.ScrollableHeight <= 0.5, $"The pane scrolls by {scroll.ScrollableHeight}.");

    var tabs = Descendants<TabControl>(panel).Single();
    var metadata = Descendants<DataGrid>(tabs).Single(grid => AutomationProperties.GetName(grid) == "Book metadata");
    var rowHeight = Descendants<DataGridRow>(metadata).First().ActualHeight;
    var headerHeight = Descendants<DataGridColumnHeader>(metadata).First().ActualHeight;
    var gridNeeds = headerHeight + 3 * rowHeight + 4;
    Assert.True(metadata.ActualHeight >= gridNeeds, $"Metadata grid is {metadata.ActualHeight} high, the header and 3 rows need {gridNeeds}.");
    tabs.SelectedIndex = 1;
    Settle(window);
    var chapters = Descendants<DataGrid>(tabs).Single(grid => AutomationProperties.GetName(grid) == "Chapter titles");
    Assert.True(chapters.ActualHeight >= gridNeeds, $"Chapter grid is {chapters.ActualHeight} high, the header and 3 rows need {gridNeeds}.");
    var included = (ListBox)window.FindName("IncludedList");
    var probe = new ListBox();
    for (var row = 0; row < 4; row++) probe.Items.Add("row");
    probe.Measure(new Size(200, 1000));
    Assert.True(included.ActualHeight >= probe.DesiredSize.Height, $"Track list is {included.ActualHeight} high, 4 rows need {probe.DesiredSize.Height}.");
    Assert.True(scroll.ScrollableHeight <= 0.5, $"The pane scrolls by {scroll.ScrollableHeight}.");
  });

  [Fact]
  public void WithAResultShowingTheTabsStillGetUsableHeightAtTheDefaultWindow() => RunWindow(1120, 760, async (window, model) =>
  {
    var item = await Select(window, model);
    typeof(BookQueueItemViewModel).GetProperty(nameof(BookQueueItemViewModel.Result))!.SetValue(item, ConversionResultDisplay.Create(
      new(Guid.NewGuid(), ConversionTerminalStatus.Succeeded, ConversionStage.Completed, null, null, null, null, null,
        [new("warning", ServiceDiagnosticSeverity.Warning, "Unknown warning")], [], "finished.m4b"), TimeSpan.Zero, ValidationLevel.Lightweight));
    Settle(window);
    var scroll = (ScrollViewer)window.FindName("BookPaneScroll");
    var panel = (DockPanel)window.FindName("SelectedBookPanel");
    var tabs = Descendants<TabControl>(panel).Single();
    Assert.Equal(Visibility.Visible, ((Border)window.FindName("ConversionResultCard")).Visibility);
    Assert.True(scroll.ScrollableHeight <= 0.5, $"The pane scrolls by {scroll.ScrollableHeight}.");
    Assert.True(tabs.ActualHeight >= 150, $"The tabs are {tabs.ActualHeight} high, card {((Border)window.FindName("ConversionResultCard")).ActualHeight}.");
  });

  [Fact]
  public void AtTheMinimumWindowTheTabsKeepUsableHeight() => RunWindow(760, 520, async (window, model) =>
  {
    await Select(window, model);
    var panel = (DockPanel)window.FindName("SelectedBookPanel");
    var tabs = Descendants<TabControl>(panel).Single();
    Assert.True(tabs.ActualHeight >= 150, $"The tabs are {tabs.ActualHeight} high.");
    var scroll = (ScrollViewer)window.FindName("BookPaneScroll");
    Assert.True(scroll.ScrollableHeight > 0, "The pane should fall back to scrolling when the window is this short.");
  });

  private static async Task<BookQueueItemViewModel> Select(MainWindow window, MainWindowViewModel model)
  {
    await model.AddAsync(Path.GetFullPath("pane-fit-book"));
    var item = model.Items[^1];
    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);
    ((ListBox)window.FindName("Queue")).SelectedItem = item;
    Settle(window);
    return item;
  }

  private static void RunWindow(double width, double height, Func<MainWindow, MainWindowViewModel, Task> check) => DispatcherTestHost.Run(async _ =>
  {
    var root = Directory.CreateTempSubdirectory("booksplice-pane-").FullName;
    try
    {
      var services = new BookSplice.Gui.Bootstrap.GuiServices(new QueueTests.Analyzer(), new BookHeaderTests.FakePlanner(), new Idle(), new NoCovers());
      var window = new MainWindow(new JsonSettingsStore(root), AppSettings.Defaults with { OutputDirectory = Path.Combine(root, "out") }, services)
      { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, Width = width, Height = height };
      window.Show();
      Settle(window);
      var model = (MainWindowViewModel)window.DataContext;
      try { await check(window, model); }
      finally
      {
        foreach (var item in model.Items) item.IsBusy = false;
        window.Close();
      }
    }
    finally { try { Directory.Delete(root, true); } catch (IOException) { } }
  });

  private sealed class NoCovers : ICoverPayloadOpener
  {
    public ValueTask<Stream> OpenReadAsync(CoverPayloadReference payload, CancellationToken cancellationToken) => throw new InvalidOperationException("No cover should be read.");
  }

  private sealed class Idle : IConversionService
  {
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null) => throw new InvalidOperationException("No conversion should run.");
  }

  private static void Settle(Window window)
  {
    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    window.UpdateLayout();
    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    window.UpdateLayout();
  }

  private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
  {
    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
    {
      var child = VisualTreeHelper.GetChild(parent, index);
      if (child is T match) yield return match;
      foreach (var descendant in Descendants<T>(child)) yield return descendant;
    }
  }
}
