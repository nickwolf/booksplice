using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class QueueTests
{
  [Fact]
  public async Task DuplicateDropKeepsOneItemAndReportsIt()
  {
    var model = new MainWindowViewModel(new Analyzer(), new Converter(), AppSettings.Defaults);
    await model.AddAsync(Path.GetFullPath("book"));
    await model.AddAsync(Path.GetFullPath("book") + Path.DirectorySeparatorChar);
    Assert.Single(model.Items);
    Assert.Contains("already", model.Message);
  }

  [Fact]
  public async Task AmbiguousOrderBlocksConversionUntilExplicitChoice()
  {
    var converter = new Converter();
    var model = new MainWindowViewModel(new Analyzer(), converter, AppSettings.Defaults);
    await model.AddAsync(Path.GetFullPath("book"));
    var item = model.Items[0];
    Assert.False(item.CanConvert);
    await model.ConvertAsync(item);
    Assert.Null(converter.Request);
    await model.ResolveOrderAsync(item, OrderCandidateId.Metadata);
    Assert.True(item.CanConvert);
    await model.ConvertAsync(item);
    Assert.Equal(OrderCandidateId.Metadata, converter.Request!.AnalysisOptions!.SelectedOrderCandidateId);
    Assert.Equal("Complete", item.Status);
    Assert.True(item.CanRemove);
    Assert.Equal("finished.m4b", item.PublishedPath);
  }

  [Fact]
  public async Task EditedAndClearedMetadataReachConversionRequest()
  {
    var converter = new Converter();
    var model = new MainWindowViewModel(new Analyzer(), converter, AppSettings.Defaults);
    await model.AddAsync(Path.GetFullPath("book"));
    var item = model.Items[0];
    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);
    item.Metadata.Single(field => field.Field == SemanticField.BookTitle).Value = "Edited";
    item.Metadata.Single(field => field.Field == SemanticField.Author).Value = "";
    await model.ConvertAsync(item);
    Assert.Equal("Edited", converter.Request!.Options.MetadataEdits[SemanticField.BookTitle].Value);
    Assert.Null(converter.Request.Options.MetadataEdits[SemanticField.Author].Value);
  }

  [Fact]
  public async Task CancellationWaitsForServiceAndNeverExposesOutput()
  {
    var converter = new Converter(wait: true);
    var model = new MainWindowViewModel(new Analyzer(), converter, AppSettings.Defaults);
    await model.AddAsync(Path.GetFullPath("book"));
    var item = model.Items[0];
    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);
    var running = model.ConvertAsync(item);
    Assert.True(item.IsBusy);
    item.Cancel();
    await running;
    Assert.Equal("Cancelled", item.Status);
    Assert.Null(item.PublishedPath);
    Assert.False(item.IsBusy);
    Assert.True(item.CanConvert);
  }

  [Fact]
  public async Task QueueLimitsConcurrentBooksAndContinuesAfterOneFailure()
  {
    var converter = new BoundedConverter();
    using var model = new MainWindowViewModel(new Analyzer(), converter, AppSettings.Defaults with { ConversionJobs = 4 });
    model.Settings = AppSettings.Defaults with { ConversionJobs = 2 };
    for (var index = 0; index < 4; index++)
    {
      await model.AddAsync(Path.GetFullPath($"book{index}"));
      await model.ResolveOrderAsync(model.Items[index], OrderCandidateId.NaturalPath);
    }
    await model.ConvertAllAsync();
    Assert.InRange(converter.Maximum, 1, 2);
    Assert.Equal(3, model.Items.Count(item => item.Status == "Complete"));
    Assert.Single(model.Items, item => item.Status == "ExecutionFailed");
  }

  private sealed class BoundedConverter : IConversionService
  {
    private int _active;
    public int Maximum { get; private set; }
    public async Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      var count = Interlocked.Increment(ref _active);
      Maximum = Math.Max(Maximum, count);
      await Task.Delay(50, cancellationToken);
      Interlocked.Decrement(ref _active);
      var failed = request.Source.EndsWith("book0", StringComparison.Ordinal);
      return new(Guid.NewGuid(), failed ? ConversionTerminalStatus.ExecutionFailed : ConversionTerminalStatus.Succeeded,
        ConversionStage.Completed, null, null, null, null, null, [], [], failed ? null : "finished.m4b");
    }
  }

  private sealed class Analyzer : IBookAnalyzer
  {
    public Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      var natural = new OrderCandidate(OrderCandidateId.NaturalPath, ["1.mp3", "2.mp3"], true, OrderConfidence.High, []);
      var tags = new OrderCandidate(OrderCandidateId.Metadata, ["2.mp3", "1.mp3"], true, OrderConfidence.High, []);
      var chosen = options?.SelectedOrderCandidateId is { } id ? (id == OrderCandidateId.Metadata ? tags : natural) : null;
      return Task.FromResult(new BookAnalysis(chosen is null ? BookAnalysisStatus.NeedsDecision : BookAnalysisStatus.Ready,
        inputPath, [], new OrderResolution(chosen is null ? OrderStatus.NeedsDecision : OrderStatus.Resolved, chosen, [natural, tags], []),
        new BookMetadata(new Dictionary<SemanticField, AggregatedValue> { [SemanticField.Author] = new(SemanticField.Author, AggregationState.Consistent, "Author", []) },
          new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()),
        new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([], 0), []));
    }
  }

  private sealed class Converter(bool wait = false) : IConversionService
  {
    public ConversionRequest? Request { get; private set; }
    public async Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      Request = request;
      if (wait) await Task.Delay(Timeout.Infinite, cancellationToken);
      return new(Guid.NewGuid(), ConversionTerminalStatus.Succeeded, ConversionStage.Completed, null, null, null, null, null, [], [], "finished.m4b");
    }
  }
}
