using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using BookSplice.Core.Analysis;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.Gui.Diagnostics;
using BookSplice.Gui.Updates;

namespace BookSplice.Gui.ViewModels;

public sealed class MainWindowViewModel(IBookAnalyzer analyzer, IConversionService conversion, AppSettings settings, IConversionPlanner? planner = null, GuiOperationGate? gate = null, MetadataProfileCatalog? profiles = null) : ObservableModel, IDisposable
{
  private SemaphoreSlim _slots = new(settings.ConversionJobs ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 6));
  private string _message = "";
  public ObservableCollection<BookQueueItemViewModel> Items { get; } = [];
  private AppSettings _settings = settings;
  public AppSettings Settings
  {
    get => _settings;
    set
    {
      if (Items.Any(item => item.IsBusy)) throw new InvalidOperationException("Wait for active jobs before changing settings.");
      _settings = value;
      _slots.Dispose();
      _slots = new(value.ConversionJobs ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 6));
    }
  }
  public string Message { get => _message; private set { _message = value; Changed(); } }

  // Registers running work with the update gate. Returns null when an update is being prepared.
  private IDisposable? Enter(string operation)
  {
    if (gate is null) return NoGate.Instance;
    if (gate.IsFrozen) { Message = "BookSplice is preparing an update. Try again if the update is cancelled."; return null; }
    return gate.Enter(operation);
  }

  private sealed class NoGate : IDisposable
  {
    public static NoGate Instance { get; } = new();
    public void Dispose() { }
  }

  public async Task AddAsync(string path)
  {
    using var operation = Enter("add");
    if (operation is null) return;
    try
    {
      path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
      if (Items.Any(item => string.Equals(item.Source, path, StringComparison.OrdinalIgnoreCase)))
      { Message = "This source is already in the queue."; return; }
      var item = new BookQueueItemViewModel(path, Settings, profiles);
      Items.Add(item);
      Message = "";
      await AnalyzeAsync(item);
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
    { Message = exception.Message; }
  }

  public async Task ResolveOrderAsync(BookQueueItemViewModel item, OrderCandidateId candidate)
  {
    if (item.IsBusy || !item.CanEdit || !item.OrderCandidates.Any(value => value.Id == candidate)) return;
    using var operation = Enter("order");
    if (operation is null) return;
    item.SelectedOrder = candidate;
    await AnalyzeAsync(item);
  }

  private async Task AnalyzeAsync(BookQueueItemViewModel item)
  {
    item.Result = null;
    item.PublishedPath = null;
    item.IsCancellationRequested = false;
    item.IsBusy = true;
    item.Status = "Analyzing";
    using var cancellation = new CancellationTokenSource();
    item.Cancellation = cancellation;
    var source = item.Source;
    var createChapters = item.Settings.CreateChapters;
    var analysisOptions = new BookAnalysisOptions(item.SelectedOrder);
    try
    {
      var result = await Task.Run(() => analyzer.AnalyzeAsync(source, createChapters,
        analysisOptions, cancellation.Token));
      item.SetAnalysis(result);
    }
    catch (OperationCanceledException) { item.Status = "Cancelled"; }
    catch (Exception exception) { item.Status = "Analysis failed"; item.Details = exception.Message; }
    finally { item.Cancellation = null; item.IsBusy = false; }
  }

  public async Task ConvertAsync(BookQueueItemViewModel item)
  {
    if (!item.CanConvert) return;
    using var operation = Enter("conversion");
    if (operation is null) return;
    item.IsBusy = true;
    item.Status = "Converting";
    item.Result = null;
    item.PublishedPath = null;
    item.Percent = 0;
    item.IsProgressIndeterminate = true;
    item.IsCancellationRequested = false;
    using var cancellation = new CancellationTokenSource();
    item.Cancellation = cancellation;
    using var tracked = ActiveJobRegistry.Shared.Track(cancellation);
    var acquired = false;
    var finished = false;
    Guid? registeredJob = null;
    var elapsed = new Stopwatch();
    var requestedValidation = item.Settings.ValidationLevel;
    var display = new ConversionProgressDisplay();
    var dispatcher = Dispatcher.CurrentDispatcher;
    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    timer.Tick += (_, _) =>
    {
      if (elapsed.IsRunning && ReferenceEquals(item.Cancellation, cancellation)) ApplySnapshot();
    };
    var progress = new Progress<ConversionProgress>(value =>
    {
      if (dispatcher.CheckAccess()) ApplyProgress(value);
      else dispatcher.BeginInvoke(() => ApplyProgress(value));
    });
    try
    {
      item.Status = "Waiting";
      item.Details = "Waiting for a conversion slot…";
      await _slots.WaitAsync(cancellation.Token);
      acquired = true;
      item.Status = "Converting";
      elapsed.Start();
      ApplySnapshot();
      timer.Start();
      var request = new ConversionRequest(item.Source, item.Settings.CreateChapters, item.CreateOptions(singleConversionJob: true),
        AnalysisOptions: new BookAnalysisOptions(item.SelectedOrder), ExpectedSourcePaths: item.Analysis!.OrderedFiles.Select(file => file.FullPath).ToArray());
      var run = Task.Run(() => conversion.ConvertAsync(request, cancellation.Token, progress));
      tracked.SetTask(run);
      var result = await run;
      elapsed.Stop();
      timer.Stop();
      item.Result = ConversionResultDisplay.Create(result, elapsed.Elapsed, requestedValidation);
      item.Status = result.Status == ConversionTerminalStatus.Succeeded ? "Complete" : result.Status.ToString();
      item.PublishedPath = item.Result.PublishedPath;
      item.Details = item.Result.Details;
      item.IsProgressIndeterminate = false;
      if (result.Status == ConversionTerminalStatus.Succeeded) item.Percent = 100;
    }
    catch (OperationCanceledException) { ShowExceptionResult(ConversionTerminalStatus.Cancelled, null); }
    catch (Exception exception) { ShowExceptionResult(ConversionTerminalStatus.UnexpectedFailure, exception.Message); }
    finally
    {
      finished = true;
      if (registeredJob is { } job) ActiveJobRegistry.Shared.Remove(job);
      timer.Stop(); elapsed.Stop(); if (acquired) _slots.Release(); item.Cancellation = null; item.IsBusy = false;
    }

    void ShowExceptionResult(ConversionTerminalStatus status, string? message)
    {
      elapsed.Stop();
      timer.Stop();
      var result = new ConversionServiceResult(Guid.NewGuid(), status, acquired ? ConversionStage.Execution : ConversionStage.Planning,
        null, null, null, null, null, message is null ? [] : [new("conversion.unexpected", ServiceDiagnosticSeverity.Error, message)], [], null);
      item.Result = ConversionResultDisplay.Create(result, elapsed.Elapsed, requestedValidation);
      item.Status = status == ConversionTerminalStatus.Cancelled ? "Cancelled" : "Conversion failed";
      item.Details = item.Result.Details;
      item.IsProgressIndeterminate = false;
    }

    void ApplyProgress(ConversionProgress value)
    {
      if (!finished && registeredJob is null && value.JobId is { } jobId)
      {
        registeredJob = jobId;
        ActiveJobRegistry.Shared.Add(jobId);
      }
      if (!elapsed.IsRunning || !ReferenceEquals(item.Cancellation, cancellation) || item.Result is not null) return;
      if (!display.Report(value, elapsed.Elapsed)) return;
      item.Status = display.PhaseLabel;
      ApplySnapshot();
    }

    void ApplySnapshot()
    {
      var snapshot = display.Snapshot(elapsed.Elapsed);
      item.IsProgressIndeterminate = snapshot.Percent is null;
      item.Percent = snapshot.Percent ?? 0;
      item.Details = display.Describe(elapsed.Elapsed);
    }
  }

  public async Task PreviewAsync(BookQueueItemViewModel item)
  {
    if (planner is null || !item.CanConvert || item.Analysis is null) return;
    using var operation = Enter("preview");
    if (operation is null) return;
    item.Result = null;
    item.IsBusy = true;
    try
    {
      var result = await Task.Run(() => planner.CreateAsync(item.Analysis, item.CreateOptions(singleConversionJob: true), CancellationToken.None));
      item.Details = result.Plan is { } plan ? DescribePlan(plan) : DescribeRefusal(result);
    }
    catch (Exception exception) { item.Details = exception.Message; }
    finally { item.IsBusy = false; }
  }

  private static string DescribePlan(ConversionPlan plan)
  {
    var explanation = StrategyExplanation.Create(plan.Strategy, plan.StrategyReasonCodes);
    var lines = new List<string> { $"Output: {plan.OutputPath}", $"Audio: {DescribeAudio(plan)}", explanation.Summary };
    if (explanation.Reasons.Count > 0) { lines.Add("Why re-encode:"); lines.AddRange(explanation.Reasons.Select(reason => $"  - {reason}")); }
    lines.AddRange(SpaceEstimateDescription.Lines(plan.Space));
    lines.Add($"Validation: {plan.ValidationLevel} | Metadata: {MetadataProfileCatalog.PublicLabel(plan.MetadataProfileId)}");
    return string.Join(Environment.NewLine, lines);
  }

  private static string DescribeRefusal(ConversionPlanningResult result)
  {
    var lines = result.Diagnostics.Select(diagnostic => diagnostic.Message).ToList();
    if (result.Space is { } space) lines.AddRange(SpaceEstimateDescription.Lines(space));
    return string.Join(Environment.NewLine, lines);
  }

  private static string DescribeAudio(ConversionPlan plan) => plan.Strategy == AudioStrategy.AacStreamCopy
    ? $"AAC stream copy | {plan.ChannelLayout}"
    : $"{plan.Strategy} | {plan.QualityProfile.DisplayName} {plan.QualityProfile.AudioBitrateKbps} kbps | {plan.ChannelLayout}";

  public void Dispose() => _slots.Dispose();

  public async Task ConvertAllAsync()
  {
    await Task.WhenAll(Items.Where(item => item.CanConvert).ToArray().Select(ConvertAsync));
  }
}
