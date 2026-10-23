using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Execution;
using BookSplice.Core.Settings;
using BookSplice.Gui.Updates;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class UpdateEntryPointTests
{
  [Fact]
  public void PendingAddBlocksTheFreezeAndFrozenGateDeniesNewWorkAfterTheFinalCheck() => DispatcherTestHost.Run(async dispatcher =>
  {
    var gate = new GuiOperationGate(dispatcher);
    var analyzer = new BlockingAnalyzer();
    var model = new MainWindowViewModel(analyzer, new UnusedConverter(), AppSettings.Defaults, gate: gate);
    var add = model.AddAsync(Path.GetFullPath("book"));
    Assert.Equal(1, gate.ActiveCount);
    Assert.Null(gate.TryFreeze(0));
    analyzer.Release();
    await add;
    Assert.Equal(0, gate.ActiveCount);
    model.Items.Clear();

    using var freeze = gate.TryFreeze(model.Items.Count);
    Assert.NotNull(freeze);
    await model.AddAsync(Path.GetFullPath("other"));
    Assert.Empty(model.Items);
    Assert.Contains("preparing an update", model.Message);
    Assert.Equal(1, analyzer.Calls);
    freeze.Dispose();
    await model.AddAsync(Path.GetFullPath("other"));
    Assert.Single(model.Items);
  });

  [Fact]
  public void ConversionAndPreviewEntryPointsAreDeniedWhileFrozen() => DispatcherTestHost.Run(async dispatcher =>
  {
    var gate = new GuiOperationGate(dispatcher);
    var converter = new UnusedConverter();
    var model = new MainWindowViewModel(new ReadyAnalyzer(), converter, AppSettings.Defaults, gate: gate);
    await model.AddAsync(Path.GetFullPath("book"));
    var item = model.Items[0];
    using var freeze = gate.TryFreeze(0);
    Assert.NotNull(freeze);
    await model.ConvertAsync(item);
    await model.ResolveOrderAsync(item, OrderCandidateId.Metadata);
    Assert.Equal(0, converter.Calls);
    Assert.False(item.IsBusy);
    Assert.Equal(0, gate.ActiveCount);
    freeze.Dispose();
    await model.ConvertAsync(item);
    Assert.Equal(1, converter.Calls);
  });

  private sealed class ReadyAnalyzer : IBookAnalyzer
  {
    public Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null,
      CancellationToken cancellationToken = default)
    {
      var natural = new OrderCandidate(OrderCandidateId.NaturalPath, ["1.mp3"], true, OrderConfidence.High, []);
      return Task.FromResult(new BookAnalysis(BookAnalysisStatus.Ready, inputPath, [],
        new OrderResolution(OrderStatus.Resolved, natural, [natural], []),
        new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(),
          new Dictionary<SemanticField, IReadOnlyList<string>>()),
        new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([], 0), []));
    }
  }

  private sealed class BlockingAnalyzer : IBookAnalyzer
  {
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Calls { get; private set; }
    public void Release() => _gate.TrySetResult();

    public async Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null,
      CancellationToken cancellationToken = default)
    {
      Calls++;
      await _gate.Task;
      throw new InvalidOperationException("analysis stub");
    }
  }

  private sealed class UnusedConverter : IConversionService
  {
    public int Calls { get; private set; }
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken,
      IProgress<ConversionProgress>? progress = null)
    {
      Calls++;
      throw new InvalidOperationException("conversion must not start");
    }
  }
}
