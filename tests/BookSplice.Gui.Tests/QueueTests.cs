using BookSplice.Core.Analysis;
using BookSplice.Core.Discovery;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.Gui.Diagnostics;
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
    Assert.Contains("Conversion complete", item.Details);
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
    Assert.Contains("Conversion cancelled", item.Details);
    Assert.Null(item.PublishedPath);
    Assert.False(item.IsBusy);
    Assert.True(item.CanConvert);
    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);
    Assert.Null(item.Result);
    Assert.Equal("Ready", item.QueueStatus);
  }

  [Fact]
  public async Task PerBookOptionsAreSharedByPreviewAndConversion()
  {
    var output = Directory.CreateTempSubdirectory("booksplice-options-");
    try
    {
      var converter = new Converter();
      var planner = new RecordingPlanner();
      using var model = new MainWindowViewModel(new Analyzer(), converter, AppSettings.Defaults with { ConversionJobs = 3 }, planner);
      await model.AddAsync(Path.GetFullPath("book"));
      var item = model.Items[0];
      await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);
      item.OutputDirectory = output.FullName;
      item.QualityProfileId = "efficient";
      item.ChannelPolicy = ChannelPolicy.ForceMono;
      item.ValidationLevel = ValidationLevel.Full;
      item.MetadataProfileId = "GenericMp4";

      await model.PreviewAsync(item);

      Assert.Equal(output.FullName, planner.Options!.DestinationDirectory);
      Assert.Equal("efficient", planner.Options.QualityProfile.Id);
      Assert.Equal(ChannelPolicy.ForceMono, planner.Options.Settings.ChannelPolicy);
      Assert.Equal(ValidationLevel.Full, planner.Options.Settings.ValidationLevel);
      Assert.Equal("GenericMp4", planner.Options.Settings.MetadataProfileId);
      Assert.Equal(3, planner.Options.Settings.ConversionJobs);
      Assert.Contains(Path.Combine(output.FullName, "Preview.m4b"), item.Details);
      Assert.Contains("Efficient 48 kbps | mono", item.Details);

      await model.ConvertAsync(item);

      Assert.Equal(output.FullName, converter.Request!.Options.DestinationDirectory);
      Assert.Equal(planner.Options.QualityProfile, converter.Request.Options.QualityProfile);
      Assert.Equal(planner.Options.Settings.ChannelPolicy, converter.Request.Options.Settings.ChannelPolicy);
      Assert.Equal(planner.Options.Settings.ValidationLevel, converter.Request.Options.Settings.ValidationLevel);
      Assert.Equal(planner.Options.Settings.MetadataProfileId, converter.Request.Options.Settings.MetadataProfileId);
      Assert.Equal(planner.Options.Settings.ConversionJobs, converter.Request.Options.Settings.ConversionJobs);
    }
    finally { output.Delete(true); }
  }
  [Fact]
  public async Task StreamCopyPreviewDoesNotReportUnusedTranscodeBitrate()
  {
    var planner = new RecordingPlanner(AudioStrategy.AacStreamCopy);
    using var model = new MainWindowViewModel(new Analyzer(), new Converter(), AppSettings.Defaults, planner);
    await model.AddAsync(Path.GetFullPath("book"));
    var item = model.Items[0];
    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);
    item.QualityProfileId = "preserve-more";

    await model.PreviewAsync(item);

    Assert.Contains("AAC stream copy | stereo", item.Details);
    Assert.DoesNotContain("128 kbps", item.Details);
  }
  [Fact]
  public async Task PreviewDetailsAndPublicDiagnosticsDoNotNameACustomProfile()
  {
    var catalog = MetadataProfileCatalog.BuiltIn;
    using var directory = new TemporaryProfiles();
    directory.Write("custom.json", """{"schemaVersion":1,"name":"SyntheticCustom","version":"1","mappings":[{"field":"BookTitle","keys":["TITLE"]}]}""");
    catalog = MetadataProfileCatalog.Load(directory.Root);
    using var model = new MainWindowViewModel(new Analyzer(), new Converter(), AppSettings.Defaults, new RecordingPlanner(), profiles: catalog);
    await model.AddAsync(Path.GetFullPath("book"));
    var item = model.Items[0];
    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);
    item.MetadataProfileId = "SyntheticCustom";

    await model.PreviewAsync(item);

    Assert.Contains("Metadata: Custom", item.Details);
    Assert.DoesNotContain("SyntheticCustom", item.Details);
    Assert.DoesNotContain("SyntheticCustom", DiagnosticsTextBuilder.Build(item));
  }
  [Fact]
  public async Task PerBookOutputTemplateReachesThePlannerAndPreviewShowsOutputNotes()
  {
    var planner = new RecordingPlanner(notes: [new("output.folder-skipped", AnalysisDiagnosticSeverity.Information, "The folder \"{series}\" was skipped because series is not available."), new("other.code", AnalysisDiagnosticSeverity.Information, "unrelated")]);
    using var model = new MainWindowViewModel(new Analyzer(), new Converter(), AppSettings.Defaults, planner);
    await model.AddAsync(Path.GetFullPath("book"));
    var item = model.Items[0];
    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);
    item.OutputTemplate = @"  {author}\{series}\{title} ";

    await model.PreviewAsync(item);

    Assert.Equal(@"{author}\{series}\{title}", planner.Options!.Settings.OutputTemplate);
    Assert.Contains("Note: The folder \"{series}\" was skipped because series is not available.", item.Details);
    Assert.DoesNotContain("unrelated", item.Details);
  }
  [Fact]
  public async Task CopyPreviewSaysAudioIsNotReencodedAndShowsCombinedStorage()
  {
    var planner = new RecordingPlanner(AudioStrategy.AacStreamCopy, space: new SpaceEstimate(400_000_000, 400_000_000, 880_000_000, 50_000_000_000, 49_000_000_000, "D:\\"));
    using var model = new MainWindowViewModel(new Analyzer(), new Converter(), AppSettings.Defaults, planner);
    await model.AddAsync(Path.GetFullPath("book"));
    var item = model.Items[0];
    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);

    await model.PreviewAsync(item);

    Assert.Contains("Audio is copied without re-encoding.", item.Details);
    Assert.DoesNotContain("Why re-encode", item.Details);
    Assert.Contains("Estimated space needed: 839.2 MB on D: for output and temporary files with a 10% margin (free space: 46.6 GB)", item.Details);
  }

  [Fact]
  public async Task ReencodePreviewListsReasonsAndSeparateVolumes()
  {
    var space = new SpaceEstimate(400_000_000, 400_000_000, 880_000_000, 50_000_000_000, 1, "D:\\", "C:\\", 440_000_000, 440_000_000, null);
    var planner = new RecordingPlanner(AudioStrategy.FilterConcatTranscode, ["stream-copy.codec.unsupported", "stream-copy.sample-rate.missing"], space);
    using var model = new MainWindowViewModel(new Analyzer(), new Converter(), AppSettings.Defaults, planner);
    await model.AddAsync(Path.GetFullPath("book"));
    var item = model.Items[0];
    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);

    await model.PreviewAsync(item);

    Assert.Contains("Audio will be re-encoded to AAC.", item.Details);
    Assert.Contains("Why re-encode:", item.Details);
    Assert.Contains("  - The source audio is not AAC.", item.Details);
    Assert.Contains("  - Could not confirm", item.Details);
    Assert.Contains("kbps", item.Details);
    Assert.Contains("Estimated output size: 381.5 MB on D: (free space: 46.6 GB)", item.Details);
    Assert.Contains("Estimated temporary space: 381.5 MB on C: (free space unavailable)", item.Details);
  }

  [Fact]
  public async Task InsufficientSpacePreviewStillShowsEstimates()
  {
    var space = new SpaceEstimate(400_000_000, 400_000_000, 880_000_000, 1_000, null, "D:\\");
    var planner = new RecordingPlanner(AudioStrategy.DirectTranscode, [], space, refuse: true);
    using var model = new MainWindowViewModel(new Analyzer(), new Converter(), AppSettings.Defaults, planner);
    await model.AddAsync(Path.GetFullPath("book"));
    var item = model.Items[0];
    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);

    await model.PreviewAsync(item);

    Assert.Contains("The destination does not have enough available space.", item.Details);
    Assert.Contains("Estimated space needed: 839.2 MB on D:", item.Details);
    Assert.Contains("free space: 1000 bytes", item.Details);
  }
  [Fact]
  public async Task AddManyShowsEveryPathWaitingThenAnalyzesInOrder()
  {
    var analyzer = new GatedAnalyzer();
    var model = new MainWindowViewModel(analyzer, new Converter(), AppSettings.Defaults);
    var progress = new List<string>();
    model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.BatchProgress)) progress.Add(model.BatchProgress); };
    var paths = FullPaths("a", "b", "c");

    var add = model.AddManyAsync(paths);
    await analyzer.WaitForCallAsync(0);

    Assert.Equal(3, model.Items.Count);
    Assert.Equal(["Analyzing", "Waiting", "Waiting"], model.Items.Select(item => item.Status));
    Assert.Equal("Analyzing 1 of 3", model.BatchProgress);
    Assert.True(model.IsBatchActive);
    Assert.All(model.Items.Skip(1), item => { Assert.False(item.CanConvert); Assert.True(item.CanCancel); Assert.True(item.CanRemove); });

    analyzer.Release(0);
    await analyzer.WaitForCallAsync(1);
    Assert.Equal("Analyzing 2 of 3", model.BatchProgress);
    analyzer.Release(1);
    await analyzer.WaitForCallAsync(2);
    analyzer.Release(2);
    await add;

    Assert.Equal(paths, analyzer.Calls);
    Assert.Equal(["Analyzing 1 of 3", "Analyzing 2 of 3", "Analyzing 3 of 3", ""], progress);
    Assert.False(model.IsBatchActive);
    Assert.All(model.Items, item => Assert.Equal("Choose track order", item.Status));
  }

  [Fact]
  public async Task CancellingOrRemovingAWaitingItemSkipsItsAnalysis()
  {
    var analyzer = new GatedAnalyzer();
    var model = new MainWindowViewModel(analyzer, new Converter(), AppSettings.Defaults);
    var add = model.AddManyAsync(FullPaths("a", "b", "c", "d"));
    await analyzer.WaitForCallAsync(0);

    model.Items[1].Cancel();
    model.Items.Remove(model.Items[2]);
    Assert.Equal("Cancelled", model.Items[1].Status);
    Assert.False(model.Items[1].CanCancel);
    analyzer.Release(0);
    await analyzer.WaitForCallAsync(1);
    Assert.Equal("Analyzing 4 of 4", model.BatchProgress);
    analyzer.Release(1);
    await add;

    Assert.Equal(FullPaths("a", "d"), analyzer.Calls);
    Assert.Equal("", model.BatchProgress);
    Assert.Equal(["Choose track order", "Cancelled", "Choose track order"], model.Items.Select(item => item.Status));
  }

  [Fact]
  public async Task CancellingTheAnalyzingItemMovesOnToTheNext()
  {
    var analyzer = new GatedAnalyzer();
    var model = new MainWindowViewModel(analyzer, new Converter(), AppSettings.Defaults);
    var add = model.AddManyAsync(FullPaths("a", "b"));
    await analyzer.WaitForCallAsync(0);

    model.Items[0].Cancel();
    await analyzer.WaitForCallAsync(1);
    analyzer.Release(1);
    await add;

    Assert.Equal(["Cancelled", "Choose track order"], model.Items.Select(item => item.Status));
    Assert.False(model.IsBatchActive);
  }

  [Fact]
  public async Task FailedAnalysisDoesNotEndTheBatch()
  {
    var analyzer = new GatedAnalyzer { FailOn = Path.GetFullPath("a") };
    var model = new MainWindowViewModel(analyzer, new Converter(), AppSettings.Defaults);
    var add = model.AddManyAsync(FullPaths("a", "b"));
    await analyzer.WaitForCallAsync(0);
    analyzer.Release(0);
    await analyzer.WaitForCallAsync(1);
    analyzer.Release(1);
    await add;

    Assert.Equal(["Analysis failed", "Choose track order"], model.Items.Select(item => item.Status));
    Assert.Equal("boom", model.Items[0].Details);
    Assert.Equal("", model.BatchProgress);
  }

  [Fact]
  public async Task DuplicatesAndBadPathsAreReportedWithoutEndingTheBatch()
  {
    var model = new MainWindowViewModel(new Analyzer(), new Converter(), AppSettings.Defaults);
    await model.AddAsync(Path.GetFullPath("a"));

    await model.AddManyAsync([Path.GetFullPath("a"), Path.GetFullPath("b"), Path.GetFullPath("b") + Path.DirectorySeparatorChar, "bad\0path", Path.GetFullPath("c")]);

    Assert.Equal(["a", "b", "c"], model.Items.Select(item => item.Name));
    var lines = model.Message.Split(Environment.NewLine);
    Assert.Equal(3, lines.Length);
    Assert.Contains("a is already in the queue.", lines);
    Assert.Contains("b is already in the queue.", lines);
    Assert.Contains(lines, line => line.StartsWith("bad\0path:", StringComparison.Ordinal));
    Assert.Equal("", model.BatchProgress);
  }

  private sealed class TemporaryProfiles : IDisposable
  {
    public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"BookSplice-{Guid.NewGuid():N}")).FullName;
    public void Write(string name, string content) => File.WriteAllText(Path.Combine(Root, name), content);
    public void Dispose() => Directory.Delete(Root, recursive: true);
  }
  [Fact]
  public async Task QueueConvertsOneBookAtATimeByDefault()
  {
    var converter = new BoundedConverter();
    using var model = new MainWindowViewModel(new Analyzer(), converter, AppSettings.Defaults with { ConversionJobs = 4 });
    for (var index = 0; index < 3; index++)
    {
      await model.AddAsync(Path.GetFullPath($"book{index}"));
      await model.ResolveOrderAsync(model.Items[index], OrderCandidateId.NaturalPath);
    }
    await model.ConvertAllAsync();
    Assert.Equal(1, converter.Maximum);
  }

  [Fact]
  public async Task QueueLimitsConcurrentBooksAndContinuesAfterOneFailure()
  {
    var converter = new BoundedConverter();
    using var model = new MainWindowViewModel(new Analyzer(), converter, AppSettings.Defaults with { BooksAtOnce = 4 });
    model.Settings = AppSettings.Defaults with { BooksAtOnce = 2 };
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

  private sealed class RecordingPlanner(AudioStrategy strategy = AudioStrategy.DirectTranscode, string[]? reasons = null, SpaceEstimate? space = null, bool refuse = false, AnalysisDiagnostic[]? notes = null) : IConversionPlanner
  {
    public ConversionOptions? Options { get; private set; }
    public Task<ConversionPlanningResult> CreateAsync(BookAnalysis analysis, ConversionOptions options, CancellationToken cancellationToken)
    {
      Options = options;
      var channels = options.Settings.ChannelPolicy == ChannelPolicy.ForceMono ? 1 : 2;
      var plan = new ConversionPlan([], analysis.BookMetadata, null, [], options.QualityProfile, options.Settings.ValidationLevel,
        options.CollisionPolicy, Path.Combine(options.DestinationDirectory, "Preview.m4b"), strategy, reasons ?? [],
        space ?? new SpaceEstimate(1, 1, 2, 3, 1), options.Settings.ConversionJobs ?? 6, "test", options.Settings.MetadataProfileId == MetadataProfiles.GenericMp4.Name ? MetadataProfiles.GenericMp4 : new MetadataProfile(options.Settings.MetadataProfileId, "1", [new(SemanticField.BookTitle, "TITLE")]), 44_100, channels);
      return Task.FromResult(refuse
        ? new ConversionPlanningResult(BookAnalysisStatus.Invalid, null, [new("planning.insufficient-space", AnalysisDiagnosticSeverity.Error, "The destination does not have enough available space.")], plan.Space, strategy, reasons)
        : new ConversionPlanningResult(BookAnalysisStatus.Ready, plan, notes ?? []));
    }
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

  internal sealed class Analyzer : IBookAnalyzer
  {
    public List<SourceSelection> Selections { get; } = [];
    public Task<BookAnalysis> AnalyzeAsync(SourceSelection selection, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      Selections.Add(selection);
      return AnalyzeAsync(selection.RootPath, chaptersEnabled, options, cancellationToken);
    }

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

  private static string[] FullPaths(params string[] names) => names.Select(Path.GetFullPath).ToArray();

  private sealed class GatedAnalyzer : IBookAnalyzer
  {
    private readonly List<TaskCompletionSource> _gates = [];
    private readonly List<TaskCompletionSource> _started = [];
    private readonly Analyzer _inner = new();
    public List<string> Calls { get; } = [];
    public string? FailOn { get; init; }

    public Task<BookAnalysis> AnalyzeAsync(SourceSelection selection, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default) => AnalyzeAsync(selection.RootPath, chaptersEnabled, options, cancellationToken);

    public async Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      TaskCompletionSource gate;
      lock (_gates)
      {
        Calls.Add(inputPath);
        gate = Slot(_gates, Calls.Count - 1);
        Slot(_started, Calls.Count - 1).TrySetResult();
      }
      await gate.Task.WaitAsync(cancellationToken);
      if (inputPath == FailOn) throw new InvalidOperationException("boom");
      return await _inner.AnalyzeAsync(inputPath, chaptersEnabled, options, cancellationToken);
    }

    public Task WaitForCallAsync(int index) { lock (_gates) return Slot(_started, index).Task.WaitAsync(TimeSpan.FromSeconds(10)); }
    public void Release(int index) { lock (_gates) Slot(_gates, index).TrySetResult(); }

    private static TaskCompletionSource Slot(List<TaskCompletionSource> list, int index)
    {
      while (list.Count <= index) list.Add(new(TaskCreationOptions.RunContinuationsAsynchronously));
      return list[index];
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
