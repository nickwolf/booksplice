using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BookSplice.Core.Analysis;
using BookSplice.Core.Covers;
using BookSplice.Core.Execution;
using BookSplice.Core.Settings;
using BookSplice.Gui.Controls;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class QueueRowTests
{
  [Fact]
  public void QueueKeysMapAltArrowsAndDelete()
  {
    Assert.Equal(QueueAction.MoveUp, QueueKeys.Resolve(Key.System, Key.Up, ModifierKeys.Alt));
    Assert.Equal(QueueAction.MoveDown, QueueKeys.Resolve(Key.System, Key.Down, ModifierKeys.Alt));
    Assert.Equal(QueueAction.MoveDown, QueueKeys.Resolve(Key.Down, Key.None, ModifierKeys.Alt));
    Assert.Equal(QueueAction.Remove, QueueKeys.Resolve(Key.Delete, Key.None, ModifierKeys.None));
    Assert.Equal(QueueAction.None, QueueKeys.Resolve(Key.Up, Key.None, ModifierKeys.None));
    Assert.Equal(QueueAction.None, QueueKeys.Resolve(Key.Delete, Key.None, ModifierKeys.Control));
  }

  [Fact]
  public void EveryRowHasNamedMoveAndRemoveButtonsThatActOnThatBook() => Run(async (window, model) =>
  {
    var one = await AddReady(model, "Book One");
    var two = await AddReady(model, "Book Two");
    var three = await AddReady(model, "Book Three");
    Settle(window);

    var buttons = RowButtons(window, two);
    Assert.Equal(["Move Book Two up", "Move Book Two down", "Remove Book Two"], buttons.Select(AutomationProperties.GetName));
    Assert.Equal(["Move Book Two up (Alt+Up)", "Move Book Two down (Alt+Down)", "Remove Book Two (Delete)"], buttons.Select(button => button.ToolTip));
    Assert.All(buttons, button => Assert.Equal(AutomationProperties.GetName(button), new ButtonAutomationPeer(button).GetName()));

    // The ends of the queue have nothing to move past, so those buttons are disabled.
    Assert.False(RowButtons(window, one)[0].IsEnabled);
    Assert.True(RowButtons(window, one)[1].IsEnabled);
    Assert.False(RowButtons(window, three)[1].IsEnabled);

    Invoke(buttons[0]);
    Assert.Equal([two, one, three], model.Items);
    Settle(window);
    Assert.False(RowButtons(window, two)[0].IsEnabled);
    Assert.True(RowButtons(window, one)[0].IsEnabled);

    Invoke(RowButtons(window, two)[1]);
    Assert.Equal([one, two, three], model.Items);

    Invoke(RowButtons(window, two)[2]);
    Assert.Equal([one, three], model.Items);
  });

  [Fact]
  public void RemoveButtonIsDisabledWhileTheBookIsRunningAndTheKeyIsIgnored() => Run(async (window, model) =>
  {
    var busy = await AddReady(model, "Book One");
    var idle = await AddReady(model, "Book Two");
    busy.IsBusy = true;
    Settle(window);
    Assert.False(RowButtons(window, busy)[2].IsEnabled);
    Assert.True(RowButtons(window, idle)[2].IsEnabled);

    var queue = (ListBox)window.FindName("Queue");
    queue.SelectedItem = busy;
    PressDelete(queue);
    Assert.Equal([busy, idle], model.Items);
  });

  [Fact]
  public void DeleteOnTheFocusedQueueRemovesTheSelectedBookAndSelectsTheNext() => Run(async (window, model) =>
  {
    var one = await AddReady(model, "Book One");
    var two = await AddReady(model, "Book Two");
    var three = await AddReady(model, "Book Three");
    var queue = (ListBox)window.FindName("Queue");
    queue.SelectedItem = two;
    Settle(window);
    PressDelete(queue);
    Assert.Equal([one, three], model.Items);
    Assert.Same(three, queue.SelectedItem);
  });

  [Fact]
  public void ConvertSelectedAndCancelFollowTheSelectedBook() => Run(async (window, model) =>
  {
    var convert = (Button)window.FindName("ConvertSelectedButton");
    var cancel = (Button)window.FindName("CancelSelectedButton");
    var queue = (ListBox)window.FindName("Queue");
    var ready = await AddReady(model, "Book One");
    var undecided = await AddUndecided(model, "Book Two");
    var running = await AddReady(model, "Book Three");
    running.IsBusy = true;
    running.Status = "Converting";

    queue.SelectedItem = ready;
    Settle(window);
    Assert.True(convert.IsVisible);
    Assert.False(cancel.IsVisible);

    queue.SelectedItem = undecided;
    Settle(window);
    Assert.False(convert.IsVisible, "a book that is not ready has nothing to convert");
    Assert.False(cancel.IsVisible);

    queue.SelectedItem = running;
    Settle(window);
    Assert.False(convert.IsVisible);
    Assert.True(cancel.IsVisible);

    running.IsBusy = false;
    running.Status = "Cancelled";
    Settle(window);
    Assert.False(cancel.IsVisible);
    Assert.True(convert.IsVisible);

    queue.SelectedItem = null;
    Settle(window);
    Assert.False(convert.IsVisible || cancel.IsVisible);
  });

  [Fact]
  public void SelectedBookRowKeepsCopyDiagnosticsAndDropsTheQueueActions() => Run(async (window, model) =>
  {
    var queue = (ListBox)window.FindName("Queue");
    queue.SelectedItem = await AddReady(model, "Book One");
    Settle(window);
    var buttons = Descendants<Button>(window).Where(button => button is not IconButton && button.IsVisible).Select(button => button.Content).OfType<string>().ToArray();
    Assert.Contains("Copy dia_gnostics", buttons);
    Assert.DoesNotContain(buttons, text => text.Contains("Remove", StringComparison.Ordinal) || text.Contains("Move", StringComparison.Ordinal));
    var keys = RowButtonNames.Select(name => (string)((Button)window.FindName(name)).Content)
      .Select(text => text[(text.IndexOf('_', StringComparison.Ordinal) + 1)..][..1]).ToArray();
    Assert.Empty(keys.Intersect(TakenKeys, StringComparer.OrdinalIgnoreCase));
    Assert.Equal(keys.Length, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
  });

  [Fact]
  public void DefaultQueueWidthFitsAFortyFiveCharacterNameAndStatusOnOneLineEach() => Run(async (window, model) =>
  {
    const string LongName = "Brandon Sanderson - The Way of Kings, Part";
    Assert.Equal(42, LongName.Length);
    var book = await AddReady(model, LongName + " 11");
    book.Thumbnail = Thumbnail(50, 100);
    var running = await AddReady(model, LongName + " 12");
    running.IsBusy = true;
    running.IsCancellationRequested = true;
    running.Status = "Converting";
    Settle(window);
    Assert.Equal(1120, window.Width);
    var queue = (ListBox)window.FindName("Queue");
    Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(queue));
    var oneLine = new TextBlock { Text = "Ag", FontWeight = FontWeights.SemiBold };
    oneLine.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    foreach (var item in new[] { book, running })
    {
      var texts = Descendants<TextBlock>(Container(window, item)).Where(text => text.Text == item.Name || text.Text == item.QueueStatus).ToArray();
      Assert.Equal(2, texts.Length);
      Assert.All(texts, text =>
      {
        Assert.True(text.ActualHeight < oneLine.DesiredSize.Height * 1.5, $"'{text.Text}' wrapped: {text.ActualHeight} vs {oneLine.DesiredSize.Height}");
        Assert.True(text.ActualWidth > 0);
      });
      foreach (var button in RowButtons(window, item))
        Assert.True(button.TranslatePoint(new Point(button.ActualWidth, 0), queue).X <= queue.ActualWidth, "a row button is cut off");
    }
  });

  [Fact]
  public void ThumbnailShowsWhenTheBookHasACoverAndTakesNoSpaceWhenItDoesNot() => Run(async (window, model) =>
  {
    var with = await AddReady(model, "Book One");
    var without = await AddReady(model, "Book Two");
    with.Thumbnail = Thumbnail(100, 50);
    Settle(window);
    var shown = Thumb(window, with);
    Assert.True(shown.IsVisible);
    // A 2:1 picture fills the 40 pixel width and keeps its shape.
    Assert.Equal(40, shown.ActualWidth);
    Assert.Equal(20, shown.ActualHeight);
    Assert.Equal(Stretch.Uniform, shown.Stretch);
    var hidden = Thumb(window, without);
    Assert.False(hidden.IsVisible);
    Assert.Equal(0, hidden.ActualWidth);
    with.Thumbnail = null;
    Settle(window);
    Assert.False(Thumb(window, with).IsVisible);
  });

  [Fact]
  public void TheWindowFeedsTheAnalyzedCoverIntoTheRowThumbnail() => Run(async (window, model) =>
  {
    var item = await AddUndecided(model, "Book One");
    await WaitFor(() => item.Thumbnail is not null);
    Settle(window);
    var image = Thumb(window, item);
    Assert.True(image.IsVisible);
    Assert.Same(item.Thumbnail, image.Source);
  }, new CoverAnalyzer(true), new PngOpener(Png(400, 200)));

  [Fact]
  public async Task ThumbnailIsDecodedSmallFrozenAndAspectPreservedFromTheAnalyzedCover()
  {
    var opener = new PngOpener(Png(400, 200));
    var model = new MainWindowViewModel(new CoverAnalyzer(true), new Idle(), AppSettings.Defaults, coverPayloads: opener);
    await model.AddAsync(Path.GetFullPath("Book One"));
    var item = model.Items[0];
    await WaitFor(() => item.Thumbnail is not null);
    var bitmap = Assert.IsAssignableFrom<BitmapSource>(item.Thumbnail);
    Assert.True(bitmap.IsFrozen);
    Assert.Equal(80, bitmap.PixelWidth);
    Assert.Equal(40, bitmap.PixelHeight);
    Assert.True(item.HasThumbnail);
    Assert.Equal(1, opener.Opens);

    // A tall cover is limited by height instead.
    var tall = new MainWindowViewModel(new CoverAnalyzer(true, 200, 400), new Idle(), AppSettings.Defaults, coverPayloads: new PngOpener(Png(200, 400)));
    await tall.AddAsync(Path.GetFullPath("Book Two"));
    await WaitFor(() => tall.Items[0].Thumbnail is not null);
    var tallBitmap = Assert.IsAssignableFrom<BitmapSource>(tall.Items[0].Thumbnail);
    Assert.Equal(80, tallBitmap.PixelHeight);
    Assert.Equal(40, tallBitmap.PixelWidth);
  }

  [Fact]
  public async Task BooksWithoutACoverOrWithoutAnOpenerHaveNoThumbnail()
  {
    var opener = new PngOpener(Png(10, 10));
    var plain = new MainWindowViewModel(new CoverAnalyzer(false), new Idle(), AppSettings.Defaults, coverPayloads: opener);
    await plain.AddAsync(Path.GetFullPath("Book One"));
    Assert.Null(plain.Items[0].Thumbnail);
    Assert.False(plain.Items[0].HasThumbnail);
    Assert.Equal(0, opener.Opens);

    var noOpener = new MainWindowViewModel(new CoverAnalyzer(true), new Idle(), AppSettings.Defaults);
    await noOpener.AddAsync(Path.GetFullPath("Book Two"));
    Assert.Null(noOpener.Items[0].Thumbnail);
  }

  [Fact]
  public async Task AnUnreadableCoverLeavesTheRowWithoutAThumbnail()
  {
    var model = new MainWindowViewModel(new CoverAnalyzer(true), new Idle(), AppSettings.Defaults, coverPayloads: new PngOpener([1, 2, 3]));
    await model.AddAsync(Path.GetFullPath("Book One"));
    await Task.Delay(300);
    Assert.Null(model.Items[0].Thumbnail);
    Assert.Equal("Choose track order", model.Items[0].Status);
  }

  private static readonly string[] RowButtonNames = ["ConvertSelectedButton", "CancelSelectedButton", "CopyDiagnosticsButton"];
  private static readonly string[] TakenKeys = ["f", "i", "b", "q", "y", "p", "s", "v", "m", "t", "c", "o"];

  private static async Task WaitFor(Func<bool> condition)
  {
    for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(50);
    Assert.True(condition(), "timed out");
  }

  private static BitmapSource Thumbnail(int width, int height)
  {
    var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, new byte[width * height * 3], width * 3);
    source.Freeze();
    return source;
  }

  private static byte[] Png(int width, int height)
  {
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(Thumbnail(width, height)));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
  }

  private static void PressDelete(ListBox queue)
  {
    var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(queue), 0, Key.Delete) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
    queue.RaiseEvent(args);
    Assert.Equal(Keyboard.Modifiers == ModifierKeys.None, args.Handled);
  }

  // UI Automation runs the click on the dispatcher later, so let it run before looking at the result.
  private static void Invoke(Button button)
  {
    ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    button.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
  }

  private static DependencyObject Container(Window window, BookQueueItemViewModel item)
  {
    var queue = (ListBox)window.FindName("Queue");
    return queue.ItemContainerGenerator.ContainerFromItem(item) ?? throw new InvalidOperationException("The row has not been generated.");
  }

  private static Image Thumb(Window window, BookQueueItemViewModel item) => Descendants<Image>(Container(window, item)).Single(image => image.Name == "RowThumbnail");

  private static IconButton[] RowButtons(Window window, BookQueueItemViewModel item) => Descendants<IconButton>(Container(window, item)).ToArray();

  private static async Task<BookQueueItemViewModel> AddReady(MainWindowViewModel model, string name)
  {
    var item = await AddUndecided(model, name);
    await model.ResolveOrderAsync(item, BookSplice.Core.Ordering.OrderCandidateId.NaturalPath);
    return item;
  }

  private static async Task<BookQueueItemViewModel> AddUndecided(MainWindowViewModel model, string name)
  {
    await model.AddAsync(Path.GetFullPath(name));
    return model.Items[^1];
  }

  private static void Settle(Window window)
  {
    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    window.UpdateLayout();
  }

  // The window builds its own view model from the services, as the app does, so the real wiring is what gets exercised.
  private static void Run(Func<MainWindow, MainWindowViewModel, Task> check, IBookAnalyzer? analyzer = null, ICoverPayloadOpener? covers = null) => DispatcherTestHost.Run(async _ =>
  {
    var root = Directory.CreateTempSubdirectory("booksplice-queue-row-").FullName;
    try
    {
      var services = new BookSplice.Gui.Bootstrap.GuiServices(analyzer ?? new QueueTests.Analyzer(), new NoPlanner(), new Idle(), covers ?? new PngOpener(Png(2, 2)));
      var window = new MainWindow(new JsonSettingsStore(root), AppSettings.Defaults, services) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
      window.Show();
      window.UpdateLayout();
      var model = (MainWindowViewModel)window.DataContext;
      try { await check(window, model); }
      finally
      {
        // Closing the window while a book is marked busy opens a message box that would hang the test.
        foreach (var item in model.Items) item.IsBusy = false;
        window.Close();
      }
    }
    finally { Directory.Delete(root, true); }
  });

  private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
  {
    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
    {
      var child = VisualTreeHelper.GetChild(parent, index);
      if (child is T match) yield return match;
      foreach (var descendant in Descendants<T>(child)) yield return descendant;
    }
  }

  private sealed class NoPlanner : BookSplice.Core.Planning.IConversionPlanner
  {
    public Task<BookSplice.Core.Planning.ConversionPlanningResult> CreateAsync(BookAnalysis analysis, BookSplice.Core.Planning.ConversionOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
  }

  private sealed class Idle : IConversionService
  {
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null) => throw new NotSupportedException();
  }

  private sealed class PngOpener(byte[] bytes) : ICoverPayloadOpener
  {
    private int _opens;
    public int Opens => _opens;

    public ValueTask<Stream> OpenReadAsync(CoverPayloadReference payload, CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref _opens);
      return new(new MemoryStream(bytes));
    }
  }

  private sealed class CoverAnalyzer(bool withCover, int width = 400, int height = 200) : BookSplice.Core.Analysis.IBookAnalyzer
  {
    private readonly QueueTests.Analyzer _inner = new();

    public Task<BookAnalysis> AnalyzeAsync(BookSplice.Core.Discovery.SourceSelection selection, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default) => AnalyzeAsync(selection.RootPath, chaptersEnabled, options, cancellationToken);

    public async Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      var analysis = await _inner.AnalyzeAsync(inputPath, chaptersEnabled, options, cancellationToken);
      if (!withCover) return analysis;
      var cover = new CoverCandidate("hash", CoverOrigin.ExternalFile, Path.Combine(inputPath, "cover.png"), null, "image/png", width, height, CoverSemanticType.FrontCover, true);
      return new BookAnalysis(analysis.Status, analysis.SourceRootPath, analysis.SourceRootName, analysis.OrderedFiles, analysis.OrderResolution, analysis.BookMetadata,
        new CoverDiscoveryResult([cover], [], cover), analysis.Chapters, analysis.Diagnostics);
    }
  }
}
