using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class SelectedTracksTests
{
  private static string[] Tracks(params string[] names) => names.Select(name => Path.GetFullPath(Path.Combine("audiobook", name))).ToArray();

  [Fact]
  public async Task OneBookChoiceQueuesASingleItemForExactlyTheSelectedFiles()
  {
    var analyzer = new QueueTests.Analyzer();
    var model = new MainWindowViewModel(analyzer, new Converter(), AppSettings.Defaults);
    var files = Tracks("Disc1/01.mp3", "Disc2/01.mp3");
    IReadOnlyList<string>? offered = null;

    await model.AddFilesAsync(files, value => { offered = value; return FileImportChoice.OneBook; });

    Assert.Equal(files, offered);
    var item = Assert.Single(model.Items);
    Assert.Equal("audiobook", item.Name);
    Assert.Equal(files, item.Selection!.Paths);
    Assert.Equal(item.Selection, Assert.Single(analyzer.Selections));
  }

  [Fact]
  public async Task NothingIsQueuedWhileTheChoiceIsPending()
  {
    var model = new MainWindowViewModel(new QueueTests.Analyzer(), new Converter(), AppSettings.Defaults);
    var queuedWhenAsked = -1;

    await model.AddFilesAsync(Tracks("01.mp3", "02.mp3"), _ => { queuedWhenAsked = model.Items.Count; return FileImportChoice.OneBook; });

    Assert.Equal(0, queuedWhenAsked);
  }

  [Fact]
  public async Task SeparateBooksChoiceKeepsTheOneItemPerFileBehaviour()
  {
    var analyzer = new QueueTests.Analyzer();
    var model = new MainWindowViewModel(analyzer, new Converter(), AppSettings.Defaults);
    var files = Tracks("01.mp3", "02.mp3", "03.mp3");

    await model.AddFilesAsync(files, _ => FileImportChoice.SeparateBooks);

    Assert.Equal(files, model.Items.Select(item => item.Source));
    Assert.All(model.Items, item => Assert.Null(item.Selection));
    Assert.Empty(analyzer.Selections);
  }

  [Fact]
  public async Task CancelQueuesNothing()
  {
    var analyzer = new QueueTests.Analyzer();
    var model = new MainWindowViewModel(analyzer, new Converter(), AppSettings.Defaults);

    await model.AddFilesAsync(Tracks("01.mp3", "02.mp3"), _ => FileImportChoice.Cancel);

    Assert.Empty(model.Items);
    Assert.Empty(analyzer.Selections);
  }

  [Fact]
  public async Task OneFileIsQueuedWithoutAsking()
  {
    var model = new MainWindowViewModel(new QueueTests.Analyzer(), new Converter(), AppSettings.Defaults);

    await model.AddFilesAsync(Tracks("01.mp3"), _ => throw new InvalidOperationException("The choice is only for several files."));

    Assert.Null(Assert.Single(model.Items).Selection);
  }

  [Fact]
  public async Task AGroupWithTheSameFilesIsRejectedWhateverTheOrder()
  {
    var model = new MainWindowViewModel(new QueueTests.Analyzer(), new Converter(), AppSettings.Defaults);
    var files = Tracks("01.mp3", "02.mp3");
    await model.AddFilesAsync(files, _ => FileImportChoice.OneBook);

    await model.AddFilesAsync(files.Reverse().ToArray(), _ => FileImportChoice.OneBook);

    Assert.Single(model.Items);
    Assert.Contains("already in the queue", model.Message);
  }

  [Fact]
  public async Task AGroupAndAFolderAtTheSamePathAreDifferentItems()
  {
    var model = new MainWindowViewModel(new QueueTests.Analyzer(), new Converter(), AppSettings.Defaults);
    await model.AddFilesAsync(Tracks("01.mp3", "02.mp3"), _ => FileImportChoice.OneBook);

    await model.AddAsync(Path.GetFullPath("audiobook"));
    await model.AddAsync(Path.GetFullPath("audiobook"));

    Assert.Equal(2, model.Items.Count);
    Assert.Single(model.Items, item => item.Selection is null);
  }

  [Fact]
  public async Task AddFolderStillAnalyzesThePathAndSendsNoSelection()
  {
    var analyzer = new QueueTests.Analyzer();
    var converter = new Converter();
    var model = new MainWindowViewModel(analyzer, converter, AppSettings.Defaults);
    await model.AddAsync(Path.GetFullPath("audiobook"));
    await model.ResolveOrderAsync(model.Items[0], OrderCandidateId.NaturalPath);

    await model.ConvertAsync(model.Items[0]);

    Assert.Empty(analyzer.Selections);
    Assert.Null(converter.Request!.Selection);
    Assert.Equal(Path.GetFullPath("audiobook"), converter.Request.Source);
  }

  [Fact]
  public async Task ConversionRequestCarriesTheSelectionAndTheReviewedIdentities()
  {
    var files = Tracks("01.mp3", "02.mp3");
    var identities = files.Select((path, index) => new SourceIdentity(path, 10 + index, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "1:2")).ToArray();
    var converter = new Converter();
    var model = new MainWindowViewModel(new IdentityAnalyzer(identities), converter, AppSettings.Defaults);
    await model.AddFilesAsync(files, _ => FileImportChoice.OneBook);

    await model.ConvertAsync(model.Items[0]);

    Assert.Equal(model.Items[0].Selection, converter.Request!.Selection);
    Assert.Equal(identities, converter.Request.ExpectedSources);
    Assert.Equal(files, converter.Request.ExpectedSourcePaths);
  }

  private sealed class IdentityAnalyzer(SourceIdentity[] identities) : IBookAnalyzer
  {
    public Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public Task<BookAnalysis> AnalyzeAsync(SourceSelection selection, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      var files = identities.Select(identity => new SourceFile(identity.Path, Path.GetFileName(identity.Path), new MediaProbeResult([], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1m), identity)).ToArray();
      var natural = new OrderCandidate(OrderCandidateId.NaturalPath, files.Select(file => file.RelativePath), true, OrderConfidence.High, []);
      return Task.FromResult(new BookAnalysis(BookAnalysisStatus.Ready, selection.RootPath, selection.Name, files, new OrderResolution(OrderStatus.Resolved, natural, [natural], []),
        new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()),
        new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([], 0), []));
    }
  }

  private sealed class Converter : IConversionService
  {
    public ConversionRequest? Request { get; private set; }

    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      Request = request;
      return Task.FromResult(new ConversionServiceResult(Guid.NewGuid(), ConversionTerminalStatus.Succeeded, ConversionStage.Completed, null, null, null, null, null, [], [], "finished.m4b"));
    }
  }
}
