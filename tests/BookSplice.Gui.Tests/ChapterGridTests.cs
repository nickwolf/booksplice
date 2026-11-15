using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class ChapterGridTests
{
  private const long Second = 1_000_000;
  private static readonly string[] Tracks = ["a.mp3", "b.mp3"];

  [Fact]
  public void TheGridShowsTheNewColumnsAndTheHeadingIsChapters() => RunWindow(async (window, model) =>
  {
    await AddAsync(window, model);
    var grid = Grid(window);
    Assert.Equal(["Start", "End", "Length", "Title", "Source", "Problem"], grid.Columns.Select(column => column.Header));
    Assert.Equal(DataGridSelectionMode.Single, grid.SelectionMode);
    Assert.Equal("Chapters", System.Windows.Automation.AutomationProperties.GetName(grid));
    Assert.Contains(Descendants<TextBlock>(window), text => text.Text == "Chapters");
    Assert.DoesNotContain(Descendants<TextBlock>(window), text => text.Text == "Chapter titles");
  });

  [Fact]
  public void SplitFromTheInlinePanelAddsARowAndUpdatesEndAndLength() => RunWindow(async (window, model) =>
  {
    var book = await AddAsync(window, model);
    var grid = Grid(window);
    grid.SelectedIndex = 0;
    Settle(window);
    Click(window, "ChapterSplitButton");
    var panel = (StackPanel)window.FindName("SplitPanel");
    var time = (TextBox)window.FindName("SplitTime");
    Assert.Equal(Visibility.Visible, panel.Visibility);
    Assert.Equal("0:00:25.000", time.Text);

    time.Text = "0:10";
    Click(window, "SplitConfirmButton");

    Assert.Equal(Visibility.Collapsed, panel.Visibility);
    Assert.Equal(3, grid.Items.Count);
    Assert.Equal(["0:00:00.000", "0:00:10.000", "0:00:50.000"], book.ChapterRows.Select(row => row.Start));
    Assert.Equal(["0:00:10.000", "0:00:50.000", "0:01:40.000"], book.ChapterRows.Select(row => row.End));
    Assert.Equal(["0:00:10.000", "0:00:40.000", "0:00:50.000"], book.ChapterRows.Select(row => row.Length));
    Assert.Equal(0, grid.SelectedIndex);
    Assert.Equal("", Message(window));
  });

  [Fact]
  public void SplitNeedsASelectionAndEnterAndEscapeDriveThePanel() => RunWindow(async (window, model) =>
  {
    var book = await AddAsync(window, model);
    var grid = Grid(window);
    grid.SelectedIndex = -1;
    Settle(window);
    Assert.False(Button(window, "ChapterSplitButton").IsEnabled);
    grid.SelectedIndex = 1;
    Settle(window);
    Assert.True(Button(window, "ChapterSplitButton").IsEnabled);
    Click(window, "ChapterSplitButton");
    var time = (TextBox)window.FindName("SplitTime");
    Assert.Equal("0:01:15.000", time.Text);
    Press(time, Key.Escape);
    Assert.Equal(Visibility.Collapsed, ((StackPanel)window.FindName("SplitPanel")).Visibility);
    Assert.Equal(2, book.ChapterRows.Count);

    Click(window, "ChapterSplitButton");
    time.Text = "1:15";
    Press(time, Key.Enter);
    Assert.Equal(3, book.ChapterRows.Count);
    Assert.Equal(Visibility.Collapsed, ((StackPanel)window.FindName("SplitPanel")).Visibility);
  });

  [Fact]
  public void ABadSplitTimeShowsAMessageAndChangesNothing() => RunWindow(async (window, model) =>
  {
    var book = await AddAsync(window, model);
    Grid(window).SelectedIndex = 0;
    Settle(window);
    Click(window, "ChapterSplitButton");
    var time = (TextBox)window.FindName("SplitTime");

    time.Text = "soon";
    Click(window, "SplitConfirmButton");
    Assert.Equal("Type a time like 1:02:03.500.", Message(window));
    Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("SplitPanel")).Visibility);

    time.Text = "0:00";
    Click(window, "SplitConfirmButton");
    Assert.NotEqual("", Message(window));
    Assert.NotEqual("Type a time like 1:02:03.500.", Message(window));
    Assert.Equal(2, book.ChapterRows.Count);
    Assert.False(book.HasChapterEdits);
    Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("SplitPanel")).Visibility);
  });

  [Fact]
  public void MergeWithNextWorksAndIsDisabledOnTheLastRow() => RunWindow(async (window, model) =>
  {
    var book = await AddAsync(window, model);
    var grid = Grid(window);
    grid.SelectedIndex = 1;
    Settle(window);
    Assert.False(Button(window, "ChapterMergeButton").IsEnabled);
    grid.SelectedIndex = 0;
    Settle(window);
    Assert.True(Button(window, "ChapterMergeButton").IsEnabled);

    Click(window, "ChapterMergeButton");

    Assert.Single(book.ChapterRows);
    Assert.Equal("0:01:40.000", book.ChapterRows[0].Length);
    Assert.Equal(0, grid.SelectedIndex);
    Assert.False(Button(window, "ChapterMergeButton").IsEnabled);
  });

  [Fact]
  public void ResetIsEnabledOnlyAfterAnEditAndUndoAndCtrlZStepBack() => RunWindow(async (window, model) =>
  {
    var book = await AddAsync(window, model);
    var grid = Grid(window);
    grid.SelectedIndex = 0;
    Settle(window);
    Assert.False(Button(window, "ChapterResetButton").IsEnabled);
    Assert.False(Button(window, "ChapterUndoButton").IsEnabled);

    Click(window, "ChapterMergeButton");
    Assert.True(Button(window, "ChapterResetButton").IsEnabled);
    Assert.True(Button(window, "ChapterUndoButton").IsEnabled);

    Click(window, "ChapterResetButton");
    Assert.False(book.HasChapterEdits);
    Assert.False(Button(window, "ChapterResetButton").IsEnabled);
    Assert.Equal(2, grid.Items.Count);

    Click(window, "ChapterUndoButton");
    Assert.Single(book.ChapterRows);
    Click(window, "ChapterUndoButton");
    Assert.Equal(2, book.ChapterRows.Count);
    Assert.False(Button(window, "ChapterUndoButton").IsEnabled);

    grid.SelectedIndex = 0;
    Click(window, "ChapterSplitButton");
    Click(window, "SplitConfirmButton");
    Assert.Equal(3, book.ChapterRows.Count);
    // Ctrl+Z is bound to the Undo command, and Keyboard.Modifiers cannot be faked, so the command itself is run.
    Assert.True(ApplicationCommands.Undo.CanExecute(null, grid));
    ApplicationCommands.Undo.Execute(null, grid);
    Settle(window);
    Assert.Equal(2, book.ChapterRows.Count);
    Assert.False(book.CanUndoChapters);
    Assert.False(ApplicationCommands.Undo.CanExecute(null, grid));
  });

  [Fact]
  public void CommittingAStartCellMovesTheStartAndARejectedStartShowsTheProblem() => RunWindow(async (window, model) =>
  {
    var book = await AddAsync(window, model);
    var grid = Grid(window);

    EditCell(window, grid, 1, 0, "0:00:40");
    Assert.Equal(["0:00:00.000", "0:00:40.000"], book.ChapterRows.Select(row => row.Start));
    Assert.Equal("", Message(window));
    Assert.Equal(1, grid.SelectedIndex);

    EditCell(window, grid, 0, 0, "0:00:05");
    Assert.Equal(["0:00:00.000", "0:00:40.000"], book.ChapterRows.Select(row => row.Start));
    Assert.NotEqual("", Message(window));
    Assert.Equal(0, grid.SelectedIndex);
    Assert.Equal("0:00:00.000", Text(grid, 0, 0));

    EditCell(window, grid, 1, 0, "later");
    Assert.Equal("Type a time like 1:02:03.500.", Message(window));
    Assert.Equal("0:00:40.000", Text(grid, 1, 0));
  });

  [Fact]
  public void EditingATitleOnAnUneditedBookStillUpdatesTheChapterTitle() => RunWindow(async (window, model) =>
  {
    var book = await AddAsync(window, model);
    var grid = Grid(window);

    EditCell(window, grid, 1, 3, "The Ending");

    Assert.Equal("The Ending", book.Chapters[1].Title);
    Assert.Equal("The Ending", book.CreateOptions().ChapterTitles!["b.mp3"]);
    Assert.False(book.HasChapterEdits);
    Assert.Equal("The Ending", Text(grid, 1, 3));
    Assert.Equal(1, grid.SelectedIndex);
  });

  [Fact]
  public void ABookThatCannotEditDisablesTheChapterControls() => RunWindow(async (window, model) =>
  {
    var book = await AddAsync(window, model);
    Grid(window).SelectedIndex = 0;
    Settle(window);
    Assert.True(Effective(Button(window, "ChapterSplitButton")));
    book.IsBusy = true;
    Settle(window);
    Assert.False(Effective(Button(window, "ChapterSplitButton")));
    Assert.False(Effective(Button(window, "ChapterMergeButton")));
    Assert.False(Effective(Button(window, "ChapterResetButton")));
    Assert.False(Effective(Button(window, "ChapterUndoButton")));
    Assert.False(Effective(Grid(window)));
  });

  private static void EditCell(MainWindow window, DataGrid grid, int row, int column, string text)
  {
    grid.SelectedIndex = row;
    grid.CurrentCell = new DataGridCellInfo(grid.Items[row], grid.Columns[column]);
    grid.ScrollIntoView(grid.Items[row], grid.Columns[column]);
    grid.UpdateLayout();
    grid.Focus();
    Assert.True(grid.BeginEdit());
    var box = Descendants<TextBox>(grid).First(candidate => candidate.IsVisible);
    box.Text = text;
    Assert.True(grid.CommitEdit(DataGridEditingUnit.Cell, true));
    Settle(window);
  }

  private static string Text(DataGrid grid, int row, int column)
  {
    grid.UpdateLayout();
    return Descendants<TextBlock>(grid.Columns[column].GetCellContent(grid.Items[row])).First().Text;
  }

  private static bool Effective(UIElement element) => element.IsEnabled && (System.Windows.Media.VisualTreeHelper.GetParent(element) is not UIElement parent || Effective(parent));

  private static DataGrid Grid(MainWindow window) => (DataGrid)window.FindName("ChapterGrid");
  private static Button Button(MainWindow window, string name) => (Button)window.FindName(name);
  private static string Message(MainWindow window) => ((TextBlock)window.FindName("ChapterEditMessage")).Text;

  private static void Click(MainWindow window, string name)
  {
    var button = Button(window, name);
    Assert.True(button.IsEnabled, name);
    button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    Settle(window);
  }

  private static void Press(UIElement target, Key key)
  {
    var source = PresentationSource.FromVisual(target)!;
    target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
  }

  private static async Task<BookQueueItemViewModel> AddAsync(MainWindow window, MainWindowViewModel model)
  {
    await model.AddAsync(Path.GetFullPath("chapter-grid-book"));
    var item = model.Items[^1];
    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);
    ((ListBox)window.FindName("Queue")).SelectedItem = item;
    Settle(window);
    ShowTab(window);
    return item;
  }

  private static void ShowTab(MainWindow window)
  {
    Descendants<TabControl>(window).Single().SelectedIndex = 1;
    Settle(window);
  }

  private static void Settle(Window window)
  {
    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    window.UpdateLayout();
  }

  private static void RunWindow(Func<MainWindow, MainWindowViewModel, Task> check) => DispatcherTestHost.Run(async _ =>
  {
    var root = Directory.CreateTempSubdirectory("booksplice-chapters-").FullName;
    try
    {
      var services = new BookSplice.Gui.Bootstrap.GuiServices(new ChapterAnalyzer(), new BookHeaderTests.FakePlanner(), new TracksAndChaptersTests.NoConverter(), new TracksAndChaptersTests.NoCovers());
      var window = new MainWindow(new JsonSettingsStore(root), AppSettings.Defaults with { OutputDirectory = Path.Combine(root, "out") }, services)
      { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
      window.Show();
      window.UpdateLayout();
      var model = (MainWindowViewModel)window.DataContext;
      model.HeaderDebounce = TimeSpan.Zero;
      try { await check(window, model); }
      finally
      {
        foreach (var item in model.Items) item.IsBusy = false;
        window.Close();
      }
    }
    finally { try { Directory.Delete(root, true); } catch (IOException) { } }
  });

  private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
  {
    for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
    {
      var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
      if (child is T match) yield return match;
      foreach (var descendant in Descendants<T>(child)) yield return descendant;
    }
  }

  private static SourceFile File(string root, string name) => new(Path.Combine(root, name), name,
    new MediaProbeResult([new AudioTrack(0, "mp3", "audio", 1, 44100, 1m, new Rational(1, 44100), new Dictionary<string, string>())], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1m),
    new SourceIdentity(Path.Combine(root, name), 1, DateTime.UnixEpoch, name));

  // One ready book of two 50 second tracks.
  private sealed class ChapterAnalyzer : IBookAnalyzer
  {
    public Task<BookAnalysis> AnalyzeAsync(SourceSelection selection, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default) => AnalyzeAsync(selection.RootPath, chaptersEnabled, options, cancellationToken);

    public Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      var natural = new OrderCandidate(OrderCandidateId.NaturalPath, Tracks, true, OrderConfidence.High, []);
      var entries = Tracks.Select((name, index) => new ChapterEntry(index * 50 * Second, (index + 1) * 50 * Second, $"Chapter {index + 1}", $"Chapter {index + 1}", name)).ToArray();
      var metadata = new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
      return Task.FromResult(new BookAnalysis(BookAnalysisStatus.Ready, inputPath, "book", Tracks.Select(name => File(inputPath, name)),
        new OrderResolution(OrderStatus.Resolved, natural, [natural], []), metadata, new CoverDiscoveryResult([], [], null), ChapterPlan.Valid(entries, 100 * Second), [], [], Tracks));
    }
  }
}
