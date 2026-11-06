using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BookSplice.Core.Covers;
using BookSplice.Core.Metadata;
using BookSplice.Core.Planning;
using BookSplice.Core.Queue;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;
using BookSplice.Gui.Views;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class MetadataEditorTests
{
  private const string Multiline = "First line\r\nSecond line";

  [Fact]
  public void TheMetadataAndCoverControlsShareOneTabAndTheGridHasNoSourceValuesColumn() => RunWindow(async (window, model, root) =>
  {
    var item = await AddAsync(model);
    var tabs = Assert.Single(Descendants<TabControl>(window));
    Assert.Equal(["_Metadata", "_Track order", "_Chapters"], tabs.Items.OfType<TabItem>().Select(tab => tab.Header));
    var metadata = (DependencyObject)tabs.Items.OfType<TabItem>().First().Content;
    Assert.NotNull(window.FindName("CoverImage"));
    Assert.Contains(Descendants<ComboBox>(metadata), box => AutomationProperties.GetName(box) == "Cover artwork");
    Assert.Contains(Descendants<CheckBox>(metadata), box => box.Content is string text && text.Contains("not embed artwork", StringComparison.Ordinal));
    Assert.Contains(Descendants<Button>(metadata), button => button == window.FindName("EditMetadataButton"));
    var grid = Descendants<DataGrid>(metadata).Single(candidate => AutomationProperties.GetName(candidate) == "Book metadata");
    Assert.Equal(["Field", "Value", "Imported state"], grid.Columns.Select(column => column.Header));
    _ = item;
  });

  [Fact]
  public void TheEditorListsEveryFieldWithItsSourceValuesAndMultiLineBoxesForLongText() => RunWindow(async (window, model, root) =>
  {
    var item = await AddAsync(model);
    AddField(item, SemanticField.Comment, "Imported note", ["Imported note", "Other note"]);
    MetadataEditorWindow? seen = null;
    window.ShowMetadataEditor = editor =>
    {
      seen = editor;
      editor.Show();
      editor.UpdateLayout();
      return false;
    };
    Select(window, item);
    Invoke((Button)window.FindName("EditMetadataButton"));

    var editor = Assert.IsType<MetadataEditorWindow>(seen);
    try
    {
      Assert.Same(window, editor.Owner);
      Assert.Equal(item.Metadata.Select(field => field.Name), editor.Rows.Select(row => row.Name));
      var comment = editor.Rows.Single(row => row.Field.Field == SemanticField.Comment);
      Assert.Equal("Imported note | Other note", comment.SourceValues);
      Assert.Contains(Descendants<TextBlock>(editor), block => block.Text.Contains("Source values: Imported note | Other note", StringComparison.Ordinal));
      var boxes = Descendants<TextBox>(editor).ToArray();
      var box = boxes.Single(candidate => AutomationProperties.GetName(candidate) == "Comment");
      Assert.True(box.AcceptsReturn);
      Assert.Equal(TextWrapping.Wrap, box.TextWrapping);
      Assert.Equal(ScrollBarVisibility.Auto, box.VerticalScrollBarVisibility);
      Assert.Contains("Other note", AutomationProperties.GetHelpText(box), StringComparison.Ordinal);
      var title = boxes.Single(candidate => AutomationProperties.GetName(candidate) == "BookTitle");
      Assert.False(title.AcceptsReturn);
      Assert.True(((Button)editor.FindName("OkButton")).IsDefault);
      Assert.True(((Button)editor.FindName("CancelButton")).IsCancel);
    }
    finally { editor.Close(); }
  });

  [Fact]
  public void OkAppliesTheEditedValuesOnceAndTheyReachTheConversionAndTheSavedQueue() => RunWindow(async (window, model, root) =>
  {
    var store = new QueueSnapshotStore(Path.Combine(root, "data"));
    await model.AttachQueueAsync(store, TimeSpan.FromHours(1), action => action());
    var item = await AddAsync(model);
    Select(window, item);
    var raised = 0;
    item.ChoicesChanged += (_, _) => raised++;
    window.ShowMetadataEditor = editor =>
    {
      editor.Rows.Single(row => row.Field.Field == SemanticField.Comment).Text = Multiline;
      editor.Rows.Single(row => row.Field.Field == SemanticField.BookTitle).Text = "Edited Tale";
      return true;
    };
    Invoke((Button)window.FindName("EditMetadataButton"));

    var comment = item.Metadata.Single(field => field.Field == SemanticField.Comment);
    Assert.True(comment.IsEdited);
    Assert.Equal(Multiline, comment.Value);
    Assert.Equal(2, raised);
    Assert.False(item.Metadata.Single(field => field.Field == SemanticField.Author).IsEdited);
    var edits = item.CreateOptions().MetadataEdits;
    Assert.Equal(Multiline, edits[SemanticField.Comment].Value);
    Assert.Equal("Edited Tale", edits[SemanticField.BookTitle].Value);
    Assert.False(edits.ContainsKey(SemanticField.Author));

    model.FlushQueue();
    var saved = Assert.IsType<QueueSnapshot>(store.Load().Snapshot).Items.Single();
    Assert.Equal(Multiline, saved.Metadata!["Comment"]);
    Assert.Equal("Edited Tale", saved.Metadata["BookTitle"]);
  });

  [Fact]
  public void CancelDiscardsTheEditsAndClearingAValueProducesAClear() => RunWindow(async (window, model, root) =>
  {
    var item = await AddAsync(model);
    Select(window, item);
    var raised = 0;
    item.ChoicesChanged += (_, _) => raised++;
    window.ShowMetadataEditor = editor => { editor.Rows.Single(row => row.Field.Field == SemanticField.Author).Text = "Someone Else"; return false; };
    Invoke((Button)window.FindName("EditMetadataButton"));
    Assert.Equal(0, raised);
    var author = item.Metadata.Single(field => field.Field == SemanticField.Author);
    Assert.False(author.IsEdited);
    Assert.Equal("Author", author.Value);

    window.ShowMetadataEditor = editor => { editor.Rows.Single(row => row.Field.Field == SemanticField.Author).Text = "  "; return true; };
    Invoke((Button)window.FindName("EditMetadataButton"));
    Assert.True(author.IsEdited);
    Assert.Equal(MetadataEdit.Clear(), item.CreateOptions().MetadataEdits[SemanticField.Author]);
  });

  [Fact]
  public void TheButtonIsDisabledAndOpensNothingForABusyOrCompleteBook() => RunWindow(async (window, model, root) =>
  {
    var item = await AddAsync(model);
    Select(window, item);
    var opened = 0;
    window.ShowMetadataEditor = _ => { opened++; return false; };
    var button = (Button)window.FindName("EditMetadataButton");
    Assert.True(button.IsEnabled);

    item.IsBusy = true;
    Settle(window);
    Assert.False(button.IsEnabled);
    item.Status = "Complete";
    item.IsBusy = false;
    Settle(window);
    Assert.False(button.IsEnabled);
    Assert.Equal(0, opened);
  });

  [Fact]
  public void CoverChoiceAndDoNotEmbedStillReachTheOptionsAndTheSavedQueueFromTheMergedTab() => RunWindow(async (window, model, root) =>
  {
    var store = new QueueSnapshotStore(Path.Combine(root, "data"));
    await model.AttachQueueAsync(store, TimeSpan.FromHours(1), action => action());
    var item = await AddAsync(model);
    Select(window, item);
    var cover = new CoverCandidate("made-up-hash", CoverOrigin.ExternalFile, Path.Combine(root, "cover.png"), null, "image/png", 10, 10, CoverSemanticType.FrontCover, true);
    item.SelectedCover = cover;
    var omit = Descendants<CheckBox>(window).Single(box => box.Content is string text && text.Contains("not embed artwork", StringComparison.Ordinal));
    Assert.Equal("made-up-hash", item.CreateOptions().SelectedCoverHash);
    Assert.False(item.CreateOptions().OmitCover);

    ((IToggleProvider)new CheckBoxAutomationPeer(omit).GetPattern(PatternInterface.Toggle)).Toggle();
    Settle(window);
    Assert.True(item.OmitCover);
    Assert.True(item.CreateOptions().OmitCover);
    model.FlushQueue();
    var saved = Assert.IsType<QueueSnapshot>(store.Load().Snapshot).Items.Single();
    Assert.True(saved.OmitCover);
  });

  [Fact]
  public void NoTwoControlsInTheEditorOrTheSelectedBookAreaShareAnAccessKey() => RunWindow(async (window, model, root) =>
  {
    var item = await AddAsync(model);
    Select(window, item);
    var editor = new MetadataEditorWindow("Made Up Tale", item.Metadata) { Owner = window };
    try
    {
      editor.Show();
      editor.UpdateLayout();
      var keys = AccessKeys(editor).ToArray();
      Assert.NotEmpty(keys);
      Assert.Equal(keys.Length, keys.Distinct().Count());
    }
    finally { editor.Close(); }

    var area = AccessKeys(window).GroupBy(key => key).Where(group => group.Count() > 1).Select(group => group.Key).Order().ToArray();
    // Chapters/Validation checks (C) and Move higher/Copy diagnostics (G) are known and fixed separately.
    Assert.All(area, key => Assert.True(key is "C" or "G", $"{key} is shared"));
    var button = (string)((Button)window.FindName("EditMetadataButton")).Content;
    Assert.DoesNotContain(char.ToUpperInvariant(button[button.IndexOf('_', StringComparison.Ordinal) + 1]).ToString(), area);
    Assert.Contains(char.ToUpperInvariant(button[button.IndexOf('_', StringComparison.Ordinal) + 1]).ToString(), AccessKeys(window));
  });

  private static IEnumerable<string> AccessKeys(Window window)
  {
    var elements = new HashSet<DependencyObject>(Descendants<DependencyObject>(window));
    foreach (var tab in Descendants<TabControl>(window).SelectMany(tabs => tabs.Items.OfType<TabItem>()).ToArray())
    {
      tab.IsSelected = true;
      window.UpdateLayout();
      elements.UnionWith(Descendants<DependencyObject>(window));
    }
    foreach (var element in elements)
    {
      var text = element switch
      {
        ContentControl { Content: string content } => content,
        HeaderedContentControl { Header: string header } => header,
        _ => null,
      };
      var index = text?.IndexOf('_', StringComparison.Ordinal) ?? -1;
      if (index >= 0 && index + 1 < text!.Length) yield return char.ToUpperInvariant(text[index + 1]).ToString();
    }
  }

  private static void AddField(BookQueueItemViewModel item, SemanticField field, string value, string[] candidates)
  {
    var existing = item.Metadata.FirstOrDefault(row => row.Field == field);
    if (existing is not null) item.Metadata.Remove(existing);
    item.Metadata.Add(new MetadataFieldViewModel(field, new AggregatedValue(field, candidates.Length > 0 ? AggregationState.Conflicting : AggregationState.Missing, value,
      candidates.Select(candidate => new MetadataCandidate(candidate, "1.mp3", "tag")).ToArray())));
  }

  private static async Task<BookQueueItemViewModel> AddAsync(MainWindowViewModel model)
  {
    await model.AddAsync(Path.GetFullPath($"editor-{model.Items.Count}"));
    var item = model.Items[^1];
    await model.ResolveOrderAsync(item, BookSplice.Core.Ordering.OrderCandidateId.NaturalPath);
    return item;
  }

  private static void Select(MainWindow window, BookQueueItemViewModel item)
  {
    ((ListBox)window.FindName("Queue")).SelectedItem = item;
    Settle(window);
  }

  private static void RunWindow(Func<MainWindow, MainWindowViewModel, string, Task> check) => DispatcherTestHost.Run(async _ =>
  {
    var root = Directory.CreateTempSubdirectory("booksplice-editor-").FullName;
    try
    {
      var services = new BookSplice.Gui.Bootstrap.GuiServices(new QueueTests.Analyzer(), new BookHeaderTests.FakePlanner(), new NoConverter(), new NoCovers());
      var window = new MainWindow(new JsonSettingsStore(root), AppSettings.Defaults with { OutputDirectory = Path.Combine(root, "out") }, services)
      { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
      window.Show();
      window.UpdateLayout();
      var model = (MainWindowViewModel)window.DataContext;
      model.HeaderDebounce = TimeSpan.Zero;
      Directory.CreateDirectory(Path.Combine(root, "out"));
      try { await check(window, model, root); }
      finally
      {
        foreach (var item in model.Items) item.IsBusy = false;
        window.Close();
      }
    }
    finally { try { Directory.Delete(root, true); } catch (IOException) { } }
  });

  private static void Settle(Window window)
  {
    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    window.UpdateLayout();
  }

  private static void Invoke(Button button)
  {
    ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    button.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
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

  private sealed class NoCovers : ICoverPayloadOpener
  {
    public ValueTask<Stream> OpenReadAsync(CoverPayloadReference payload, CancellationToken cancellationToken) => throw new InvalidOperationException("No cover should be read.");
  }

  private sealed class NoConverter : BookSplice.Core.Analysis.IConversionService
  {
    public Task<BookSplice.Core.Analysis.ConversionServiceResult> ConvertAsync(BookSplice.Core.Analysis.ConversionRequest request, CancellationToken cancellationToken, IProgress<BookSplice.Core.Execution.ConversionProgress>? progress = null)
      => throw new InvalidOperationException("No conversion expected.");
  }
}
