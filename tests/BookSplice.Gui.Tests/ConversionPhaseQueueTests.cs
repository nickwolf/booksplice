using System.Collections.Concurrent;
using System.Windows.Threading;
using BookSplice.Core.Analysis;
using BookSplice.Core.Discovery;
using BookSplice.Core.Execution;
using BookSplice.Core.Ordering;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class ConversionPhaseQueueTests
{
  [Fact]
  public Task SynchronousWorkDoesNotBlockCancel() => OnDispatcher(async dispatcher =>
  {
    var attempt = new GatedAttempt(ConversionTerminalStatus.Cancelled);
    using var model = new MainWindowViewModel(new QueueTests.Analyzer(), new ScriptedConverter(attempt), AppSettings.Defaults);
    var item = await ReadyItem(model, "blocked");
    var running = model.ConvertAsync(item);
    await attempt.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
    try
    {
      Assert.NotEqual(Environment.CurrentManagedThreadId, attempt.ExecutionThreadId);
      var tick = false;
      _ = dispatcher.BeginInvoke(() => tick = true);
      _ = dispatcher.BeginInvoke(item.Cancel);
      await Flush(dispatcher);
      Assert.True(tick);
      Assert.True(item.IsCancellationRequested);
      Assert.Contains("Cancellation requested", item.QueueStatus);
      Assert.True(attempt.CancellationToken.IsCancellationRequested);
      var capturedDestination = attempt.Request!.Options.DestinationDirectory;
      item.OutputDirectory = Path.GetFullPath("changed-after-start");
      Assert.Equal(capturedDestination, attempt.Request.Options.DestinationDirectory);

      attempt.Progress!.Report(Entry(ConversionPhase.Cleanup, 1));
      await Flush(dispatcher);
      Assert.Contains("Cleaning up", item.QueueStatus);
      Assert.Contains("Cancellation requested", item.QueueStatus);
      Assert.True(item.IsBusy);
    }
    finally { attempt.Release.TrySetResult(); }
    await running;
    Assert.Equal("Cancelled", item.Status);
    Assert.Equal("Conversion cancelled", item.QueueStatus);
  });

  [Fact]
  public Task OldAttemptCannotOverwriteRetry() => OnDispatcher(async dispatcher =>
  {
    var first = new GatedAttempt(ConversionTerminalStatus.Cancelled);
    var second = new GatedAttempt(ConversionTerminalStatus.Succeeded);
    using var model = new MainWindowViewModel(new QueueTests.Analyzer(), new ScriptedConverter(first, second), AppSettings.Defaults);
    var item = await ReadyItem(model, "retry");
    var firstRun = model.ConvertAsync(item);
    await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
    item.Cancel();
    first.Release.TrySetResult();
    await firstRun;
    var secondRun = model.ConvertAsync(item);
    await second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
    try
    {
      var job = Guid.NewGuid();
      var phase = Guid.NewGuid();
      second.Progress!.Report(Entry(ConversionPhase.Encoding, 1, job, phase) with { PhasePercent = 25 });
      await Flush(dispatcher);
      Assert.Equal("Encoding audio", item.Status);
      Assert.Equal(25, item.Percent);

      first.Progress!.Report(Entry(ConversionPhase.Publication, 90));
      await Flush(dispatcher);
      Assert.Equal("Encoding audio", item.Status);
      Assert.Equal(25, item.Percent);
      Assert.Null(item.Result);
    }
    finally { second.Release.TrySetResult(); }
    await secondRun;
    Assert.Equal("Complete", item.Status);
    Assert.Equal(100, item.Percent);
  });

  [Fact]
  public Task OldSequenceCannotOverwritePhase() => OnDispatcher(async dispatcher =>
  {
    var attempt = new GatedAttempt(ConversionTerminalStatus.Succeeded);
    using var model = new MainWindowViewModel(new QueueTests.Analyzer(), new ScriptedConverter(attempt), AppSettings.Defaults);
    var item = await ReadyItem(model, "sequence");
    var running = model.ConvertAsync(item);
    await attempt.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
    try
    {
      var job = Guid.NewGuid();
      var encoding = Guid.NewGuid();
      var metadata = Guid.NewGuid();
      attempt.Progress!.Report(Entry(ConversionPhase.Encoding, 1, job, encoding) with { PhasePercent = 80 });
      attempt.Progress.Report(Entry(ConversionPhase.MetadataWriting, 3, job, metadata));
      attempt.Progress.Report(Entry(ConversionPhase.Encoding, 2, job, encoding) with { PhasePercent = 99 });
      attempt.Progress.Report(new(null, null, null, 0, Phase: ConversionPhase.Encoding, PhaseOccurrenceId: encoding,
        PhasePercent: 100, JobId: job, Sequence: 4));
      await Flush(dispatcher);
      Assert.Equal("Writing metadata", item.Status);
      Assert.Equal(0, item.Percent);
      Assert.True(item.IsProgressIndeterminate);
      Assert.Null(item.Result);
    }
    finally { attempt.Release.TrySetResult(); }
    await running;
    Assert.Equal("Complete", item.Status);
  });

  [Fact]
  public Task LateCancelPreservesPublishedSuccess() => OnDispatcher(async dispatcher =>
  {
    var attempt = new GatedAttempt(ConversionTerminalStatus.Succeeded);
    using var model = new MainWindowViewModel(new QueueTests.Analyzer(), new ScriptedConverter(attempt), AppSettings.Defaults);
    var item = await ReadyItem(model, "late");
    var running = model.ConvertAsync(item);
    await attempt.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
    item.Cancel();
    attempt.Release.TrySetResult();
    await running;
    Assert.Equal("Complete", item.Status);
    Assert.Equal("finished.m4b", item.PublishedPath);
    Assert.Equal(100, item.Percent);
    item.Cancel();
    attempt.Progress!.Report(Entry(ConversionPhase.Cleanup, 2));
    await Flush(dispatcher);
    Assert.Equal("Complete", item.Status);
    Assert.Equal("finished.m4b", item.PublishedPath);
    Assert.DoesNotContain("Cancellation requested", item.QueueStatus);
  });

  [Fact]
  public Task CancellationWhileWaitingForSlotDoesNotStartService() => OnDispatcher(async _ =>
  {
    var first = new GatedAttempt(ConversionTerminalStatus.Succeeded);
    var converter = new ScriptedConverter(first);
    using var model = new MainWindowViewModel(new QueueTests.Analyzer(), converter, AppSettings.Defaults with { BooksAtOnce = 1 });
    var active = await ReadyItem(model, "active");
    var waiting = await ReadyItem(model, "waiting");
    var firstRun = model.ConvertAsync(active);
    await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
    try
    {
      var secondRun = model.ConvertAsync(waiting);
      Assert.Equal("Waiting", waiting.Status);
      waiting.Cancel();
      Assert.Contains("Cancellation requested", waiting.QueueStatus);
      await secondRun;
      Assert.Equal("Conversion cancelled", waiting.QueueStatus);
      Assert.Equal(1, converter.Calls);
    }
    finally { first.Release.TrySetResult(); }
    await firstRun;
  });

  [Fact]
  public Task SettledInitialAnalysisDoesNotShowPendingCancellation() => OnDispatcher(async _ =>
  {
    using var model = new MainWindowViewModel(new CancellingAnalyzer(), new ScriptedConverter(), AppSettings.Defaults);
    var adding = model.AddAsync(Path.GetFullPath("analysis-cancel"));
    var item = Assert.Single(model.Items);
    item.Cancel();
    Assert.Contains("Cancellation requested", item.QueueStatus);
    await adding;
    Assert.Equal("Cancelled", item.QueueStatus);
    Assert.False(item.IsBusy);
  });

  [Fact]
  public Task QueuedProgressRetainsPhaseSpeedAcrossStateOnlyReport() => OnDispatcher(async dispatcher =>
  {
    var attempt = new GatedAttempt(ConversionTerminalStatus.Succeeded);
    using var model = new MainWindowViewModel(new QueueTests.Analyzer(), new ScriptedConverter(attempt), AppSettings.Defaults);
    var item = await ReadyItem(model, "speed");
    var running = model.ConvertAsync(item);
    await attempt.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
    try
    {
      var job = Guid.NewGuid();
      var phase = Guid.NewGuid();
      attempt.Progress!.Report(Entry(ConversionPhase.Encoding, 1, job, phase) with { PhasePercent = 0 });
      attempt.Progress.Report(new(null, null, null, 0, Phase: ConversionPhase.Encoding, PhaseOccurrenceId: phase,
        PhasePercent: 25, PhaseSpeed: 12.3, JobId: job, Sequence: 2));
      attempt.Progress.Report(new(null, null, "continue", 0, Phase: ConversionPhase.Encoding, PhaseOccurrenceId: phase,
        JobId: job, Sequence: 3));
      await Flush(dispatcher);
      Assert.Equal("Encoding audio", item.Status);
      Assert.Equal(25, item.Percent);
      Assert.Contains("Speed: 12.3×", item.Details);
    }
    finally { attempt.Release.TrySetResult(); }
    await running;
    Assert.Contains("Conversion complete", item.Details);
  });

  private static async Task<BookQueueItemViewModel> ReadyItem(MainWindowViewModel model, string name)
  {
    await model.AddAsync(Path.GetFullPath(name));
    var item = model.Items[^1];
    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);
    Assert.True(item.CanConvert);
    return item;
  }

  private static ConversionProgress Entry(ConversionPhase phase, long sequence, Guid? job = null, Guid? occurrence = null) =>
    new(null, null, null, 0, Phase: phase, PhaseOccurrenceId: occurrence ?? Guid.NewGuid(), IsPhaseEntry: true,
      ExpectsMeasurableUpdates: phase == ConversionPhase.Encoding, JobId: job, Sequence: sequence);

  private static Task Flush(Dispatcher dispatcher) => dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

  private static Task OnDispatcher(Func<Dispatcher, Task> test)
  {
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
      var dispatcher = Dispatcher.CurrentDispatcher;
      SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
      _ = dispatcher.BeginInvoke(async () =>
      {
        try { await test(dispatcher).WaitAsync(TimeSpan.FromSeconds(15)); completion.SetResult(); }
        catch (Exception exception) { completion.SetException(exception); }
        finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
      });
      Dispatcher.Run();
    })
    {
      IsBackground = true
    };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
  }

  private sealed class GatedAttempt(ConversionTerminalStatus status)
  {
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IProgress<ConversionProgress>? Progress { get; set; }
    public ConversionRequest? Request { get; set; }
    public CancellationToken CancellationToken { get; set; }
    public int ExecutionThreadId { get; set; }
    public ConversionTerminalStatus Status { get; } = status;
  }

  private sealed class ScriptedConverter(params GatedAttempt[] attempts) : IConversionService
  {
    private readonly ConcurrentQueue<GatedAttempt> _attempts = new(attempts);
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public async Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      Interlocked.Increment(ref _calls);
      if (!_attempts.TryDequeue(out var attempt)) throw new InvalidOperationException("Unexpected conversion attempt.");
      attempt.Request = request;
      attempt.CancellationToken = cancellationToken;
      attempt.Progress = progress;
      attempt.ExecutionThreadId = Environment.CurrentManagedThreadId;
      attempt.Entered.SetResult();
      try { await attempt.Release.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
      catch (TimeoutException) { throw new TimeoutException("The controlled conversion was not released."); }
      return new ConversionServiceResult(Guid.NewGuid(), attempt.Status, ConversionStage.Completed,
        null, null, null, null, null, [], [], attempt.Status == ConversionTerminalStatus.Succeeded ? "finished.m4b" : null);
    }
  }

  private sealed class CancellingAnalyzer : IBookAnalyzer
  {
    public Task<BookAnalysis> AnalyzeAsync(SourceSelection selection, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      throw new InvalidOperationException("Analysis should have been cancelled.");
    }
  }
}
