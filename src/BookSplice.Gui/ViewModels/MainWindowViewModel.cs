using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using BookSplice.Core.Analysis;
using BookSplice.Core.Discovery;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.Gui.Diagnostics;
using BookSplice.Gui.Notifications;
using BookSplice.Gui.Updates;

namespace BookSplice.Gui.ViewModels;

public enum FileImportChoice { OneBook, SeparateBooks, Cancel }

public sealed class MainWindowViewModel(IBookAnalyzer analyzer, IConversionService conversion, AppSettings settings, IConversionPlanner? planner = null, GuiOperationGate? gate = null, MetadataProfileCatalog? profiles = null, IConversionNotifier? notifier = null) : ObservableModel, IDisposable
{
  private readonly QueueScheduler _scheduler = new(settings.BooksAtOnce ?? 1);
  private string _message = "";
  private string _batchProgress = "";
  public ObservableCollection<BookQueueItemViewModel> Items { get; } = [];
  private AppSettings _settings = settings;
  public AppSettings Settings
  {
    get => _settings;
    set
    {
      if (Items.Any(item => item.IsBusy)) throw new InvalidOperationException("Wait for active jobs before changing settings.");
      _settings = value;
      _scheduler.Capacity = value.BooksAtOnce ?? 1;
    }
  }
  public bool IsQueuePaused => _scheduler.IsPaused;
  public string QueuePauseLabel => IsQueuePaused ? "Resume _queue" : "Pause _queue";
  public string Message { get => _message; private set { _message = value; Changed(); } }
  public string BatchProgress { get => _batchProgress; private set { _batchProgress = value; Changed(); Changed(nameof(IsBatchActive)); } }
  public bool IsBatchActive => _batchProgress.Length > 0;

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

  public Task AddAsync(string path) => AddManyAsync([path]);

  // Queues every path first, then analyzes them one at a time so the whole pick shows up immediately.
  public async Task AddManyAsync(IEnumerable<string> paths)
  {
    using var operation = Enter("add");
    if (operation is null) return;
    Message = "";
    var problems = new List<string>();
    var batch = new List<BookQueueItemViewModel>();
    foreach (var raw in paths)
    {
      try
      {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
        if (Items.Any(item => item.Selection is null && string.Equals(item.Source, path, StringComparison.OrdinalIgnoreCase)))
        { problems.Add($"{Path.GetFileName(path)} is already in the queue."); continue; }
        var item = new BookQueueItemViewModel(path, Settings, profiles);
        item.MarkWaiting();
        Items.Add(item);
        batch.Add(item);
      }
      catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
      { problems.Add($"{raw}: {exception.Message}"); }
    }
    await AnalyzeBatchAsync(batch, problems);
  }

  // More than one picked file asks first, so the grouping is decided before anything enters the queue.
  public async Task AddFilesAsync(IReadOnlyList<string> files, Func<IReadOnlyList<string>, FileImportChoice> choose)
  {
    var choice = files.Count > 1 ? choose(files) : FileImportChoice.SeparateBooks;
    if (choice == FileImportChoice.Cancel) return;
    if (choice == FileImportChoice.SeparateBooks) { await AddManyAsync(files); return; }
    SourceSelection selection;
    try { selection = SourceSelection.Create(files); }
    catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
    { Message = exception.Message; return; }
    if (selection.Paths.Count == 1) { await AddManyAsync(selection.Paths); return; }
    using var operation = Enter("add");
    if (operation is null) return;
    var batch = new List<BookQueueItemViewModel>();
    var problems = new List<string>();
    if (Items.Any(item => selection.Equals(item.Selection))) problems.Add($"{selection.Name} is already in the queue with these files.");
    else
    {
      var item = new BookQueueItemViewModel(selection.RootPath, Settings, profiles) { Selection = selection };
      item.MarkWaiting();
      Items.Add(item);
      batch.Add(item);
    }
    await AnalyzeBatchAsync(batch, problems);
  }

  private async Task AnalyzeBatchAsync(List<BookQueueItemViewModel> batch, List<string> problems)
  {
    Message = string.Join(Environment.NewLine, problems);
    var resolved = 0;
    try
    {
      foreach (var item in batch)
      {
        // Cancelled or removed while waiting: skipped without analysis.
        if (!item.IsWaiting || !Items.Contains(item)) { resolved++; continue; }
        BatchProgress = $"Analyzing {resolved + 1} of {batch.Count}";
        await AnalyzeAsync(item);
        resolved++;
      }
    }
    finally
    {
      foreach (var item in batch) if (item.IsWaiting) item.CancelWaiting();
      BatchProgress = "";
    }
  }

  public async Task ResolveOrderAsync(BookQueueItemViewModel item, OrderCandidateId candidate)
  {
    if (item.IsBusy || !item.CanEdit || !item.OrderCandidates.Any(value => value.Id == candidate)) return;
    using var operation = Enter("order");
    if (operation is null) return;
    item.SelectedOrder = candidate;
    item.ReviewedOrder = null;
    await AnalyzeAsync(item);
  }

  public async Task ApplyTrackListAsync(BookQueueItemViewModel item)
  {
    if (item.IsBusy || !item.CanEdit) return;
    if (item.IncludedTracks.Count == 0) { Message = "Include at least one track."; return; }
    using var operation = Enter("order");
    if (operation is null) return;
    Message = "";
    item.ReviewedOrder = item.CreateReviewedOrder();
    await AnalyzeAsync(item);
  }

  public Task UseTrackListAsync(BookQueueItemViewModel item, OrderCandidateId? candidate)
    => item.IsTrackListEdited || candidate is null ? ApplyTrackListAsync(item) : ResolveOrderAsync(item, candidate.Value);

  private async Task AnalyzeAsync(BookQueueItemViewModel item)
  {
    item.Result = null;
    item.PublishedPath = null;
    item.IsCancellationRequested = false;
    item.IsWaiting = false;
    item.IsBusy = true;
    item.Status = "Analyzing";
    using var cancellation = new CancellationTokenSource();
    item.Cancellation = cancellation;
    var source = item.Source;
    var selection = item.Selection;
    var createChapters = item.Settings.CreateChapters;
    var analysisOptions = item.AnalysisOptions;
    try
    {
      var result = await Task.Run(() => selection is null
        ? analyzer.AnalyzeAsync(source, createChapters, analysisOptions, cancellation.Token)
        : analyzer.AnalyzeAsync(selection, createChapters, analysisOptions, cancellation.Token));
      item.SetAnalysis(result);
    }
    catch (OperationCanceledException) { item.Status = "Cancelled"; }
    catch (Exception exception) { item.Status = "Analysis failed"; item.Details = exception.Message; }
    finally { item.Cancellation = null; item.IsBusy = false; }
  }

  public async Task ConvertAsync(BookQueueItemViewModel item)
  {
    if (await ConvertCoreAsync(item) is { } finished) Notify(NotificationPolicy.ForBook(finished, Settings));
  }

  // Returns null when nothing ran, otherwise the terminal outcome for notifications.
  private async Task<BookFinished?> ConvertCoreAsync(BookQueueItemViewModel item)
  {
    if (!item.CanConvert) return null;
    using var operation = Enter("conversion");
    if (operation is null) return null;
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
      item.Details = _scheduler.IsPaused ? PausedDetails : WaitingDetails;
      // Removals since the other books queued shift positions, so refresh their ranks before adding this one.
      _scheduler.Reorder(Items);
      await _scheduler.WaitAsync(item, Items.IndexOf(item), cancellation.Token);
      acquired = true;
      cancellation.Token.ThrowIfCancellationRequested();
      item.Status = "Converting";
      elapsed.Start();
      ApplySnapshot();
      timer.Start();
      var request = new ConversionRequest(item.Source, item.Settings.CreateChapters, item.CreateOptions(),
        AnalysisOptions: item.AnalysisOptions, ExpectedSourcePaths: item.Analysis!.OrderedFiles.Select(file => file.FullPath).ToArray(),
        Selection: item.Selection, ExpectedSources: ReviewedIdentities(item.Analysis));
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
      timer.Stop(); elapsed.Stop(); if (acquired) _scheduler.Release(); item.Cancellation = null; item.IsBusy = false;
    }
    var outcome = Finished(item);
    item.Outcome = outcome.Outcome;
    return outcome;

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

  private static SourceIdentity[]? ReviewedIdentities(BookAnalysis analysis)
  {
    var identities = analysis.OrderedFiles.Select(file => file.Identity).OfType<SourceIdentity>().ToArray();
    return identities.Length == analysis.OrderedFiles.Count ? identities : null;
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
      var result = await Task.Run(() => planner.CreateAsync(item.Analysis, item.CreateOptions(), CancellationToken.None));
      item.Details = result.Plan is { } plan ? DescribePlan(plan, result.Diagnostics) : DescribeRefusal(result);
    }
    catch (Exception exception) { item.Details = exception.Message; }
    finally { item.IsBusy = false; }
  }

  private static string DescribePlan(ConversionPlan plan, IReadOnlyList<AnalysisDiagnostic>? notes = null)
  {
    var explanation = StrategyExplanation.Create(plan.Strategy, plan.StrategyReasonCodes);
    var lines = new List<string> { $"Output: {plan.OutputPath}", $"Audio: {DescribeAudio(plan)}", explanation.Summary };
    if (explanation.Reasons.Count > 0) { lines.Add("Why re-encode:"); lines.AddRange(explanation.Reasons.Select(reason => $"  - {reason}")); }
    if (notes is not null) lines.AddRange(notes.Where(note => note.Code.StartsWith("output.", StringComparison.Ordinal)).Select(note => $"Note: {note.Message}"));
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

  public void Dispose() { }

  public Task ConvertAllAsync() => ConvertBatchAsync(Items.Where(item => item.CanConvert).ToArray());

  // Successful books are never retried; CanConvert already excludes them.
  public Task RetryFailedAsync() => ConvertBatchAsync(Items.Where(item => item.Outcome == BookOutcome.Failure && item.CanConvert).ToArray());

  private async Task ConvertBatchAsync(BookQueueItemViewModel[] items)
  {
    if (items.Length == 0) return;
    var batch = notifier is null ? null : new BatchNotifier(notifier, Settings);
    var results = await Task.WhenAll(items.Select(ConvertCoreAsync));
    foreach (var finished in results) if (finished is not null) batch?.Add(finished);
    if (batch is not null) Deliver(batch.Complete);
  }

  public void PauseQueue()
  {
    _scheduler.Pause();
    RefreshWaiting();
  }

  public void ResumeQueue()
  {
    _scheduler.Resume();
    RefreshWaiting();
  }

  // Moving a book changes when it starts only if it has not started yet.
  public void Move(BookQueueItemViewModel item, int offset)
  {
    var from = Items.IndexOf(item);
    var to = from + offset;
    if (from < 0 || to < 0 || to >= Items.Count) return;
    Items.Move(from, to);
    _scheduler.Reorder(Items);
  }

  public void ClearCompleted()
  {
    foreach (var item in Items.Where(item => !item.IsBusy && item.Status == "Complete").ToArray()) Items.Remove(item);
  }

  private const string WaitingDetails = "Waiting for a conversion slot…";
  private const string PausedDetails = "Queue paused. This book starts after you resume.";

  private void RefreshWaiting()
  {
    foreach (var item in Items.Where(item => item.IsBusy && item.Status == "Waiting")) item.Details = _scheduler.IsPaused ? PausedDetails : WaitingDetails;
    Changed(nameof(IsQueuePaused));
    Changed(nameof(QueuePauseLabel));
  }

  private static BookFinished Finished(BookQueueItemViewModel item) => new(item.Source, item.Name,
    item.Status switch
    {
      "Complete" => item.Result?.HasWarnings == true ? BookOutcome.Warning : BookOutcome.Success,
      "Cancelled" => BookOutcome.Cancelled,
      _ => BookOutcome.Failure,
    }, item.PublishedPath);

  private readonly System.Collections.Concurrent.ConcurrentBag<Task> _deliveries = [];

  // Delivery runs off the UI thread and swallows its own errors, so a notification never slows or fails a conversion.
  private void Deliver(Action action) => _deliveries.Add(Task.Run(action));

  private void Notify(NotificationRequest? request)
  {
    if (notifier is not null && request is not null) Deliver(() => BatchNotifier.Deliver(notifier, request));
  }

  internal Task NotificationsDelivered() => Task.WhenAll(_deliveries);
}
