using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Settings;
using BookSplice.Gui.Controls;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class PlayerBarTests
{
  private const long Second = 1_000_000;
  private static readonly string[] Tracks = ["a.mp3", "b.mp3"];

  private sealed record Harness(MainWindow Window, MainWindowViewModel Model, FakeDecoder Decoder, List<FakePlayer> Players, BookQueueItemViewModel Book);

  [Fact]
  public void TheBarHasItsNamedButtonsAndOnlyPlayIsEnabledForAFreshBook() => RunWindow(harness =>
  {
    var window = harness.Window;
    var bar = (DependencyObject)window.FindName("PlayerBar");
    Assert.Equal(["Play", "Stop", "Play boundary", "Split here"], Descendants<IconButton>(bar).Select(button => AutomationProperties.GetName(button)));
    Assert.Equal(["Play the selected track", "Stop", "Play 5 seconds either side of the selected chapter's start", "Split the chapter at the playback position"],
      Descendants<IconButton>(bar).Select(button => (string)button.ToolTip));
    Assert.Equal("Playback position", AutomationProperties.GetName(Slider(window)));
    Assert.True(Button(window, "PlayButton").IsEnabled);
    Assert.False(Button(window, "PlayBoundaryButton").IsEnabled);
    Assert.False(Button(window, "StopButton").IsEnabled);
    Assert.False(Slider(window).IsEnabled);
    Assert.Equal(Visibility.Collapsed, Button(window, "SplitHereButton").Visibility);
    return Task.CompletedTask;
  });

  [Fact]
  public void PlayStartsTheSelectedTrackFromItsBeginningAndTheFirstWhenNoneIsSelected() => RunWindow(async harness =>
  {
    var window = harness.Window;
    Click(window, "PlayButton");
    Assert.Equal(Path.Combine(Path.GetFullPath("player-bar-book"), "a.mp3"), harness.Decoder.Calls[0].Region.Pieces[0].Path);
    Assert.Equal(0, harness.Decoder.Calls[0].Region.BookStartMicroseconds);

    ((ListBox)window.FindName("IncludedList")).SelectedIndex = 1;
    Settle(window);
    Click(window, "PlayButton");

    var window2 = harness.Decoder.Calls.Last(call => call.Region.Pieces[0].Path.EndsWith("b.mp3", StringComparison.Ordinal));
    Assert.Equal(50 * Second, window2.Region.BookStartMicroseconds);
    Assert.Equal(50 * Second, window2.Region.LengthMicroseconds);
    Assert.True(harness.Model.Preview.IsPlaying);
    Assert.Equal("0:00:00 / 0:00:50", ((TextBlock)window.FindName("PlayerPosition")).Text);
    await Task.CompletedTask;
  });

  [Fact]
  public void PlayBoundaryNeedsAChapterAndPlaysTenSecondsAroundItsStart() => RunWindow(harness =>
  {
    var window = harness.Window;
    Assert.False(Button(window, "PlayBoundaryButton").IsEnabled);
    Grid(window).SelectedIndex = 1;
    Settle(window);
    Click(window, "PlayBoundaryButton");

    var call = Assert.Single(harness.Decoder.Calls);
    Assert.Equal(45 * Second, call.Region.BookStartMicroseconds);
    Assert.Equal(10 * Second, call.Region.LengthMicroseconds);
    Assert.True(harness.Model.Preview.IsBoundary);
    Assert.Equal(0, Slider(window).Minimum);
    Assert.Equal(10, Slider(window).Maximum);
    Assert.Equal("0:00:00 / 0:00:10", ((TextBlock)window.FindName("PlayerPosition")).Text);
    return Task.CompletedTask;
  });

  [Fact]
  public void StopIsEnabledOnlyWhileSomethingPlaysAndStopsIt() => RunWindow(harness =>
  {
    var window = harness.Window;
    Assert.False(Button(window, "StopButton").IsEnabled);
    Click(window, "PlayButton");
    Assert.True(Button(window, "StopButton").IsEnabled);
    Assert.True(window.PlayerTimer.IsEnabled);
    Assert.Equal(Visibility.Visible, Button(window, "SplitHereButton").Visibility);

    Click(window, "StopButton");

    Assert.False(Button(window, "StopButton").IsEnabled);
    Assert.False(window.PlayerTimer.IsEnabled);
    Assert.Equal(Visibility.Collapsed, Button(window, "SplitHereButton").Visibility);
    Assert.False(Slider(window).IsEnabled);
    Assert.True(harness.Players[0].IsDisposed);
    Assert.True(Button(window, "PlayButton").IsEnabled);
    return Task.CompletedTask;
  });

  [Fact]
  public void ATimerTickMovesTheSliderWithoutSeeking() => RunWindow(harness =>
  {
    var window = harness.Window;
    Click(window, "PlayButton");
    var player = harness.Players[0];
    var sets = player.PositionSets.Count;

    player.Position = TimeSpan.FromSeconds(20);
    window.OnPlayerTimerTick();
    Settle(window);

    Assert.Equal(sets + 1, player.PositionSets.Count);
    Assert.Equal(50, Slider(window).Maximum);
    Assert.Equal(20, Slider(window).Value, 3);
    Assert.Equal("0:00:20 / 0:00:50", ((TextBlock)window.FindName("PlayerPosition")).Text);
    Assert.Single(harness.Players);
    return Task.CompletedTask;
  });

  [Fact]
  public void AUserSeekMapsTheSliderBackToBookTimeAndALaterWindowKeepsTheTrackOffset() => RunWindow(async harness =>
  {
    var window = harness.Window;
    await harness.Model.Preview.PlayTrackAsync(1, 20 * Second);
    Settle(window);
    var player = harness.Players[^1];
    Assert.Equal(50, Slider(window).Maximum);
    Assert.Equal(20, Slider(window).Value, 3);

    Slider(window).Value = 35;
    Settle(window);

    // Book time 85 s is 15 s into the window that starts at 70 s.
    Assert.Equal(TimeSpan.FromSeconds(15), player.PositionSets[^1]);
    Assert.Equal(85 * Second, harness.Model.Preview.CurrentBookMicroseconds);
    Assert.Single(harness.Players);
  });

  [Fact]
  public void DraggingTheThumbSeeksOnceWhenItIsReleased() => RunWindow(harness =>
  {
    var window = harness.Window;
    Click(window, "PlayButton");
    var player = harness.Players[0];
    var sets = player.PositionSets.Count;
    var slider = Slider(window);

    slider.RaiseEvent(new DragStartedEventArgs(0, 0));
    slider.Value = 10;
    slider.Value = 30;
    player.Position = TimeSpan.FromSeconds(1);
    window.OnPlayerTimerTick();
    Assert.Equal(30, slider.Value, 3);
    Assert.Equal(sets + 1, player.PositionSets.Count);

    slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
    Settle(window);

    Assert.Equal(TimeSpan.FromSeconds(30), player.PositionSets[^1]);
    Assert.Equal(sets + 2, player.PositionSets.Count);
    return Task.CompletedTask;
  });

  [Fact]
  public void SplitHereSplitsTheChapterAtThePlaybackPositionSelectsTheNewRowAndKeepsPlaying() => RunWindow(harness =>
  {
    var window = harness.Window;
    Click(window, "PlayButton");
    var player = harness.Players[0];
    player.Position = TimeSpan.FromSeconds(20);
    window.OnPlayerTimerTick();

    Click(window, "SplitHereButton");

    Assert.Equal(["0:00:00.000", "0:00:20.000", "0:00:50.000"], harness.Book.ChapterRows.Select(row => row.Start));
    Assert.Equal(1, Grid(window).SelectedIndex);
    Assert.Equal("", Message(window));
    Assert.True(harness.Model.Preview.IsPlaying);
    Assert.DoesNotContain("Close", player.Calls);
    Assert.False(player.IsDisposed);
    return Task.CompletedTask;
  });

  [Fact]
  public void SplitHereUsesTheChapterUnderThePlayerNotTheSelectedOne() => RunWindow(async harness =>
  {
    var window = harness.Window;
    Grid(window).SelectedIndex = 0;
    Settle(window);
    await harness.Model.Preview.PlayTrackAsync(1, 25 * Second);
    Settle(window);

    Click(window, "SplitHereButton");

    Assert.Equal(["0:00:00.000", "0:00:50.000", "0:01:15.000"], harness.Book.ChapterRows.Select(row => row.Start));
    Assert.Equal(2, Grid(window).SelectedIndex);
  });

  [Fact]
  public void SplitHereShowsWhyAChapterCouldNotBeSplit() => RunWindow(harness =>
  {
    var window = harness.Window;
    Click(window, "PlayButton");

    Click(window, "SplitHereButton");

    Assert.NotEqual("", Message(window));
    Assert.Equal(2, harness.Book.ChapterRows.Count);
    Assert.True(harness.Model.Preview.IsPlaying);
    return Task.CompletedTask;
  });

  [Fact]
  public void ApplyingANewTrackOrderClosesThePlayerAndPlaysTheNewFirstTrack() => RunWindow(async harness =>
  {
    var window = harness.Window;
    Click(window, "PlayButton");
    var player = harness.Players[0];
    var included = (ListBox)window.FindName("IncludedList");
    included.SelectedIndex = 0;
    Settle(window);
    Click(window, "TrackDownButton");

    await harness.Model.UseTrackListAsync(harness.Book, null);
    Settle(window);

    Assert.True(player.IsDisposed);
    Assert.False(harness.Model.Preview.IsPlaying);
    Assert.True(harness.Model.Preview.CanPlay);
    Assert.False(Button(window, "StopButton").IsEnabled);
    included.SelectedIndex = 0;
    Settle(window);
    Click(window, "PlayButton");
    Assert.EndsWith("b.mp3", harness.Decoder.Calls[^1].Region.Pieces[0].Path, StringComparison.Ordinal);
    Assert.Equal(0, harness.Decoder.Calls[^1].Region.BookStartMicroseconds);
  });

  private static Slider Slider(MainWindow window) => (Slider)window.FindName("PlayerSlider");
  private static DataGrid Grid(MainWindow window) => (DataGrid)window.FindName("ChapterGrid");
  private static Button Button(MainWindow window, string name) => (Button)window.FindName(name);
  private static string Message(MainWindow window) => ((TextBlock)window.FindName("ChapterEditMessage")).Text;

  private static void Click(MainWindow window, string name)
  {
    var button = Button(window, name);
    Assert.True(button.IsEnabled, name);
    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    Settle(window);
  }

  private static void Settle(Window window)
  {
    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    window.UpdateLayout();
  }

  private static void RunWindow(Func<Harness, Task> check) => DispatcherTestHost.Run(async _ =>
  {
    var root = Directory.CreateTempSubdirectory("booksplice-player-").FullName;
    try
    {
      var decoder = new FakeDecoder { AutoComplete = true };
      var players = new List<FakePlayer>();
      var services = new BookSplice.Gui.Bootstrap.GuiServices(new BarAnalyzer(), new BookHeaderTests.FakePlanner(), new TracksAndChaptersTests.NoConverter(), new TracksAndChaptersTests.NoCovers(),
        PreviewDecoder: decoder, PreviewRoot: Path.Combine(root, "preview"), PreviewPlayerFactory: () => { var player = new FakePlayer(); players.Add(player); return player; });
      var window = new MainWindow(new JsonSettingsStore(root), AppSettings.Defaults with { OutputDirectory = Path.Combine(root, "out") }, services)
      { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
      window.Show();
      window.UpdateLayout();
      var model = (MainWindowViewModel)window.DataContext;
      model.HeaderDebounce = TimeSpan.Zero;
      try
      {
        await model.AddAsync(Path.GetFullPath("player-bar-book"));
        var book = model.Items[^1];
        await model.ResolveOrderAsync(book, OrderCandidateId.NaturalPath);
        ((ListBox)window.FindName("Queue")).SelectedItem = book;
        Settle(window);
        Descendants<TabControl>(window).Single().SelectedIndex = 1;
        Settle(window);
        await check(new Harness(window, model, decoder, players, book));
      }
      finally
      {
        foreach (var player in players) player.Dispose();
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
    new MediaProbeResult([new AudioTrack(0, "mp3", "audio", 1, 44100, 1m, new Rational(1, 44100), new Dictionary<string, string>())], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 50m),
    new SourceIdentity(Path.Combine(root, name), 1, DateTime.UnixEpoch, name));

  // Two 50 second tracks with one chapter each, in the reviewed order when there is one.
  private sealed class BarAnalyzer : IBookAnalyzer
  {
    public Task<BookAnalysis> AnalyzeAsync(SourceSelection selection, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default) => AnalyzeAsync(selection.RootPath, chaptersEnabled, options, cancellationToken);

    public Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      var natural = new OrderCandidate(OrderCandidateId.NaturalPath, Tracks, true, OrderConfidence.High, []);
      var included = (options?.ReviewedOrder?.Included ?? Tracks).ToArray();
      var entries = included.Select((name, index) => new ChapterEntry(index * 50 * Second, (index + 1) * 50 * Second, $"Chapter {index + 1}", $"Chapter {index + 1}", name)).ToArray();
      var metadata = new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
      return Task.FromResult(new BookAnalysis(BookAnalysisStatus.Ready, inputPath, "book", included.Select(name => File(inputPath, name)),
        new OrderResolution(OrderStatus.Resolved, natural, [natural], []), metadata, new CoverDiscoveryResult([], [], null), ChapterPlan.Valid(entries, included.Length * 50 * Second), [], [], Tracks));
    }
  }
}
