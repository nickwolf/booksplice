using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using BookSplice.Core.Discovery;
using BookSplice.Core.Analysis;
using BookSplice.Core.Execution;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;
using BookSplice.Gui.Views;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class BookGroupImportTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("booksplice-groups-").FullName;

  private string Write(string relative)
  {
    var path = Path.GetFullPath(Path.Combine(_root, relative));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "audio");
    return path;
  }

  private BookGroup[] PlanGroups() => new BookGroupPlanner().Plan(_root).Groups.ToArray();

  private async Task<BookGroupPreviewViewModel> LoadedAsync()
  {
    var model = new BookGroupPreviewViewModel(_root);
    await model.LoadAsync();
    return model;
  }

  [Fact]
  public async Task PreviewListsGroupsFilesAndProblems()
  {
    Write("Book A/Disc 1/01.mp3");
    Write("Book A/Disc 2/01.mp3");
    Write("Book B/01.mp3");

    var model = await LoadedAsync();

    Assert.Equal(["Book A", "Book B"], model.Groups.Select(row => row.Title));
    Assert.Equal(["Book A\\Disc 1\\01.mp3", "Book A\\Disc 2\\01.mp3"], model.Groups[0].Lines);
    Assert.Equal("2 files in 2 folders", model.Groups[0].Summary);
    Assert.Equal("_Add 2 books", model.AddLabel);
    Assert.True(model.CanAdd);
    Assert.False(model.IsScanning);
  }

  [Fact]
  public async Task PreviewShowsPathsThatCouldNotBeInspected()
  {
    var plan = new BookGroupPlan([], [new DiscoveryDiagnostic("discovery.path-unreadable", "denied", Path.Combine(_root, "Locked")), new DiscoveryDiagnostic("discovery.reparse-point-skipped", "skipped", Path.Combine(_root, "Link"))]);
    var model = new BookGroupPreviewViewModel(_root, (_, _, _) => Task.FromResult(plan));

    await model.LoadAsync();

    Assert.Equal([$"Could not be inspected: {Path.Combine(_root, "Locked")} (denied)", $"Skipped (reparse point): {Path.Combine(_root, "Link")}"], model.NotInspected);
    Assert.False(model.CanAdd);
    Assert.Equal("No audio files were found.", model.Status);
  }

  [Fact]
  public async Task CombineJoinsSelectedGroupsAtTheFirstPositionAndSplitRestoresTheFolders()
  {
    Write("Book A/01.mp3");
    Write("Book B/Disc 1/01.mp3");
    Write("Book B/Disc 2/01.mp3");
    Write("Book C/01.mp3");
    var model = await LoadedAsync();

    Assert.False(model.Combine([model.Groups[0]]));
    Assert.True(model.Combine([model.Groups[2], model.Groups[0]]));

    Assert.Equal([Path.GetFileName(_root), "Book B"], model.Groups.Select(row => row.Title));
    Assert.Equal(2, model.Groups[0].Group.Files.Count);
    Assert.Equal("_Add 2 books", model.AddLabel);

    Assert.True(model.Split([model.Groups[1]]));
    Assert.Equal(["Disc 1", "Disc 2"], model.Groups.Skip(1).Select(row => row.Title));
    Assert.False(model.Split([model.Groups[1]]));

    Assert.True(model.Combine([model.Groups[1], model.Groups[2]]));
    Assert.Equal("Book B", model.Groups[1].Title);
    Assert.Equal(2, model.Groups[1].Group.Files.Count);
  }

  [Fact]
  public async Task RemoveDropsOnlyTheSelectedGroups()
  {
    Write("Book A/01.mp3");
    Write("Book B/01.mp3");
    Write("Book C/01.mp3");
    var model = await LoadedAsync();
    var dropped = new[] { model.Groups[0], model.Groups[2] };

    Assert.True(model.Remove(dropped));

    Assert.Equal(["Book B"], model.Groups.Select(row => row.Title));
    Assert.Equal("_Add 1 book", model.AddLabel);
    Assert.False(model.Remove(dropped));
    Assert.True(model.Remove(model.Groups.ToArray()));
    Assert.False(model.CanAdd);
  }

  [Fact]
  public async Task ScanReportsProgressAndCancelStopsIt()
  {
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var model = new BookGroupPreviewViewModel(_root, async (_, _, token) =>
    {
      started.SetResult();
      await Task.Delay(Timeout.Infinite, token);
      return new BookGroupPlan([], []);
    });

    var load = model.LoadAsync();
    await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.True(model.IsScanning);
    Assert.False(model.CanAdd);
    model.CancelScan();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
    Assert.False(model.IsScanning);
    Assert.Equal("Scan cancelled.", model.Status);
    Assert.Empty(model.Groups);
  }

  [Fact]
  public async Task AddingCreatesOneItemPerGroupWithExactlyItsFiles()
  {
    var a1 = Write("Book A/Disc 1/01.mp3");
    var a2 = Write("Book A/Disc 2/01.mp3");
    var b = Write("Book B/01.mp3");
    var analyzer = new QueueTests.Analyzer();
    var model = new MainWindowViewModel(analyzer, new NoConverter(), AppSettings.Defaults);

    await model.AddBookGroupsAsync(PlanGroups());

    Assert.Equal(["Book A", "Book B"], model.Items.Select(item => item.Name));
    Assert.Equal([a1, a2], model.Items[0].Selection!.Paths);
    Assert.Equal([b], model.Items[1].Selection!.Paths);
    Assert.Equal(2, analyzer.Selections.Count);
    Assert.All(model.Items, item => Assert.False(item.IsWaiting));
  }

  [Fact]
  public async Task AGroupMatchingAnExistingItemIsRefused()
  {
    Write("Book A/01.mp3");
    Write("Book B/01.mp3");
    var model = new MainWindowViewModel(new QueueTests.Analyzer(), new NoConverter(), AppSettings.Defaults);
    await model.AddBookGroupsAsync(PlanGroups());

    await model.AddBookGroupsAsync(PlanGroups().Append(PlanGroups()[0]));

    Assert.Equal(2, model.Items.Count);
    Assert.Contains("Book A is already in the queue", model.Message);
    Assert.Contains("Book B is already in the queue", model.Message);
  }

  [Fact]
  public async Task DuplicateGroupsWithinOneBatchAreQueuedOnce()
  {
    Write("Book A/01.mp3");
    var group = PlanGroups()[0];
    var model = new MainWindowViewModel(new QueueTests.Analyzer(), new NoConverter(), AppSettings.Defaults);

    await model.AddBookGroupsAsync([group, group]);

    Assert.Single(model.Items);
  }

  [Fact]
  public async Task LooseFilesQueueAsPlainFileItemsNamedAfterTheFile()
  {
    var loose = Write("one.mp3");
    Write("Book/01.mp3");
    var analyzer = new QueueTests.Analyzer();
    var model = new MainWindowViewModel(analyzer, new NoConverter(), AppSettings.Defaults);

    await model.AddBookGroupsAsync(PlanGroups());

    Assert.Equal(["one.mp3", "Book"], model.Items.Select(item => item.Name));
    Assert.Null(model.Items[0].Selection);
    Assert.Equal(loose, model.Items[0].Source);
    Assert.Single(analyzer.Selections);
  }

  [Fact]
  public async Task CombinedAndSplitGroupsQueueWithTheirEditedFiles()
  {
    var a = Write("Series/Book A/01.mp3");
    var b = Write("Series/Book B/01.mp3");
    var model = new MainWindowViewModel(new QueueTests.Analyzer(), new NoConverter(), AppSettings.Defaults);

    await model.AddBookGroupsAsync([BookGroup.Combine(PlanGroups())]);

    var item = Assert.Single(model.Items);
    Assert.Equal("Series", item.Name);
    Assert.Equal([a, b], item.Selection!.Paths);
  }

  [Fact]
  public async Task OrdinaryAddFolderStillQueuesTheWholeTreeAsOnePathItem()
  {
    Write("Book A/Disc 1/01.mp3");
    Write("Book A/Disc 2/01.mp3");
    var analyzer = new QueueTests.Analyzer();
    var model = new MainWindowViewModel(analyzer, new NoConverter(), AppSettings.Defaults);

    await model.AddAsync(_root);

    var item = Assert.Single(model.Items);
    Assert.Null(item.Selection);
    Assert.Equal(_root, item.Source);
    Assert.Empty(analyzer.Selections);
  }

  [Fact]
  public void PreviewWindowNamesItsControlsAndListsTheGroups()
  {
    Write("Book A/Disc 1/01.mp3");
    Write("Book A/Disc 2/01.mp3");
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        var model = new BookGroupPreviewViewModel(_root);
        model.LoadAsync().GetAwaiter().GetResult();
        var window = new BookGroupPreviewWindow(model) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        window.Show();
        var buttons = Descendants<Button>(window).ToArray();
        Assert.Equal(["Combine", "Split", "Remove", "Add books", "Cancel"], buttons.Select(button => new ButtonAutomationPeer(button).GetName()));
        var groupList = Assert.Single(Descendants<ListBox>(window), list => list.Name == "GroupList");
        Assert.Single(model.Groups);
        Assert.Single(groupList.Items);
        Assert.Equal(model.Groups.Single().Group, window.AcceptedGroups.Single());
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
  public void MainWindowOffersTheFolderOfBooksEntryBesideAddFolder()
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        var settings = Directory.CreateTempSubdirectory("booksplice-groups-settings-").FullName;
        try
        {
          var main = new MainWindow(new JsonSettingsStore(settings), AppSettings.Defaults)
          { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
          main.Show();
          var buttons = Descendants<Button>(main).Where(button => button.IsVisible).ToArray();
          Assert.Contains(buttons, button => Equals(button.Content, "Add _folder..."));
          var entry = Assert.Single(buttons, button => Equals(button.Content, "Add _jobs from folder..."));
          Assert.Equal("J", new ButtonAutomationPeer(entry).GetAccessKey());
          main.Close();
        }
        finally { Directory.Delete(settings, true); }
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

  public void Dispose() => Directory.Delete(_root, true);

  private sealed class NoConverter : IConversionService
  {
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null) => throw new NotSupportedException();
  }
}
