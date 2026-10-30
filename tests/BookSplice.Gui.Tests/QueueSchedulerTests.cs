using System.Collections.Concurrent;
using System.Windows.Threading;
using BookSplice.Core.Analysis;
using BookSplice.Core.Execution;
using BookSplice.Core.Ordering;
using BookSplice.Core.Settings;
using BookSplice.Gui.Notifications;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class QueueSchedulerTests
{
  private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

  [Fact]
  public async Task GrantsInRankOrderUpToCapacity()
  {
    var scheduler = new QueueScheduler(2);
    var a = scheduler.WaitAsync("a", 0, CancellationToken.None);
    var b = scheduler.WaitAsync("b", 1, CancellationToken.None);
    var d = scheduler.WaitAsync("d", 3, CancellationToken.None);
    var c = scheduler.WaitAsync("c", 2, CancellationToken.None);
    await Task.WhenAll(a, b).WaitAsync(Wait);
    Assert.False(c.IsCompleted);
    Assert.False(d.IsCompleted);
    scheduler.Release();
    await c.WaitAsync(Wait);
    Assert.False(d.IsCompleted);
    Assert.Equal(2, scheduler.Running);
  }

  [Fact]
  public async Task PauseStopsNewStartsAndResumeKeepsTheCap()
  {
    var scheduler = new QueueScheduler(2);
    await scheduler.WaitAsync("a", 0, CancellationToken.None).WaitAsync(Wait);
    scheduler.Pause();
    var b = scheduler.WaitAsync("b", 1, CancellationToken.None);
    var c = scheduler.WaitAsync("c", 2, CancellationToken.None);
    var d = scheduler.WaitAsync("d", 3, CancellationToken.None);
    scheduler.Release();
    Assert.Equal(0, scheduler.Running);
    Assert.False(b.IsCompleted || c.IsCompleted || d.IsCompleted);
    scheduler.Resume();
    await Task.WhenAll(b, c).WaitAsync(Wait);
    Assert.False(d.IsCompleted);
    Assert.Equal(2, scheduler.Running);
  }

  [Fact]
  public async Task ReorderChangesOnlyWaitingBooks()
  {
    var scheduler = new QueueScheduler(1);
    await scheduler.WaitAsync("a", 0, CancellationToken.None).WaitAsync(Wait);
    var b = scheduler.WaitAsync("b", 1, CancellationToken.None);
    var c = scheduler.WaitAsync("c", 2, CancellationToken.None);
    scheduler.Reorder(["c", "a", "b"]);
    Assert.Equal(1, scheduler.Running);
    scheduler.Release();
    await c.WaitAsync(Wait);
    Assert.False(b.IsCompleted);
  }

  [Fact]
  public async Task CancelledWaiterNeverTakesASlot()
  {
    var scheduler = new QueueScheduler(1);
    await scheduler.WaitAsync("a", 0, CancellationToken.None).WaitAsync(Wait);
    using var cancellation = new CancellationTokenSource();
    var b = scheduler.WaitAsync("b", 1, cancellation.Token);
    var c = scheduler.WaitAsync("c", 2, CancellationToken.None);
    cancellation.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.WaitAsync(Wait));
    scheduler.Release();
    await c.WaitAsync(Wait);
    Assert.Equal(1, scheduler.Running);
  }

  [Fact]
  public async Task SimultaneousCompletionAndCancellationNeverExceedTheCapOrStartTwice()
  {
    const int books = 200;
    const int capacity = 3;
    var scheduler = new QueueScheduler(capacity);
    var active = 0;
    var maximum = 0;
    var queued = 0;
    var cancelled = 0;
    var starts = new ConcurrentDictionary<int, int>();
    var round = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancellations = Enumerable.Range(0, books).Select(_ => new CancellationTokenSource()).ToArray();
    var runs = Enumerable.Range(0, books).Select(index => Task.Run(async () =>
    {
      var turn = scheduler.WaitAsync(index, index, cancellations[index].Token);
      Interlocked.Increment(ref queued);
      try { await turn; }
      catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); return; }
      starts.AddOrUpdate(index, 1, (_, count) => count + 1);
      InterlockedMax(ref maximum, Interlocked.Increment(ref active));
      // Every book holding a slot waits for the same round, so their releases land together.
      await Volatile.Read(ref round).Task;
      Interlocked.Decrement(ref active);
      scheduler.Release();
    })).ToArray();
    await WaitUntil(() => Volatile.Read(ref queued) == books);
    var cancelling = Task.Run(async () =>
    {
      for (var index = books - 1; index >= 0; index -= 3) { cancellations[index].Cancel(); await Task.Yield(); }
    });
    var all = Task.WhenAll(runs);
    var deadline = DateTime.UtcNow + Wait;
    while (!all.IsCompleted)
    {
      if (DateTime.UtcNow > deadline) throw new TimeoutException("The scheduler stopped granting slots.");
      await WaitUntil(() => scheduler.Running == Volatile.Read(ref active) || all.IsCompleted);
      Interlocked.Exchange(ref round, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
      await Task.Delay(1);
    }
    await cancelling;
    await all;
    Assert.InRange(maximum, 1, capacity);
    Assert.All(starts.Values, count => Assert.Equal(1, count));
    Assert.Equal(books, starts.Count + cancelled);
    Assert.InRange(cancelled, 1, (books + 2) / 3);
    Assert.Equal(0, scheduler.Running);
    foreach (var cancellation in cancellations) cancellation.Dispose();
  }

  [Fact]
  public Task ABookQueuedAfterClearingStartsBehindTheBooksAboveIt() => OnDispatcher(async () =>
  {
    var converter = new KeyedConverter();
    using var model = new MainWindowViewModel(new QueueTests.Analyzer(), converter, AppSettings.Defaults with { BooksAtOnce = 1 });
    foreach (var name in "abcd") await ReadyItem(model, name.ToString());
    var batch = model.ConvertAllAsync();
    foreach (var name in (string[])["a", "b"])
    {
      await converter.Gate(name).Entered.Task.WaitAsync(Wait);
      converter.Gate(name).Finish(ConversionTerminalStatus.Succeeded);
    }
    await converter.Gate("c").Entered.Task.WaitAsync(Wait);
    model.ClearCompleted();
    var late = await ReadyItem(model, "e");
    var single = model.ConvertAsync(late);
    Assert.Equal(["c", "d", "e"], model.Items.Select(item => item.Name));
    converter.Gate("c").Finish(ConversionTerminalStatus.Succeeded);
    await converter.Gate("d").Entered.Task.WaitAsync(Wait);
    Assert.Equal(["a", "b", "c", "d"], converter.Started);
    converter.Gate("d").Finish(ConversionTerminalStatus.Succeeded);
    await converter.Gate("e").Entered.Task.WaitAsync(Wait);
    converter.Gate("e").Finish(ConversionTerminalStatus.Succeeded);
    await Task.WhenAll(batch, single);
  });

  [Fact]
  public Task PausedQueueHoldsTheNextBookUntilResumed() => OnDispatcher(async () =>
  {
    var converter = new KeyedConverter();
    using var model = new MainWindowViewModel(new QueueTests.Analyzer(), converter, AppSettings.Defaults with { BooksAtOnce = 1 });
    var first = await ReadyItem(model, "first");
    var second = await ReadyItem(model, "second");
    var batch = model.ConvertAllAsync();
    await converter.Gate("first").Entered.Task.WaitAsync(Wait);
    model.PauseQueue();
    Assert.Equal("Resume _queue", model.QueuePauseLabel);
    Assert.Contains("paused", second.Details, StringComparison.OrdinalIgnoreCase);
    converter.Gate("first").Finish(ConversionTerminalStatus.Succeeded);
    await WaitUntil(() => first.Status == "Complete");
    Assert.Equal("Waiting", second.Status);
    Assert.Equal(["first"], converter.Started);
    model.ResumeQueue();
    await converter.Gate("second").Entered.Task.WaitAsync(Wait);
    converter.Gate("second").Finish(ConversionTerminalStatus.Succeeded);
    await batch;
    Assert.Equal(["first", "second"], converter.Started);
  });

  [Fact]
  public Task MovingAWaitingBookChangesWhichStartsNext() => OnDispatcher(async () =>
  {
    var converter = new KeyedConverter();
    using var model = new MainWindowViewModel(new QueueTests.Analyzer(), converter, AppSettings.Defaults with { BooksAtOnce = 1 });
    var first = await ReadyItem(model, "first");
    await ReadyItem(model, "second");
    var third = await ReadyItem(model, "third");
    var batch = model.ConvertAllAsync();
    await converter.Gate("first").Entered.Task.WaitAsync(Wait);
    model.Move(third, -1);
    model.Move(first, 1);
    Assert.Equal(["third", "first", "second"], model.Items.Select(item => item.Name));
    converter.Gate("first").Finish(ConversionTerminalStatus.Succeeded);
    await converter.Gate("third").Entered.Task.WaitAsync(Wait);
    converter.Gate("third").Finish(ConversionTerminalStatus.Succeeded);
    await converter.Gate("second").Entered.Task.WaitAsync(Wait);
    converter.Gate("second").Finish(ConversionTerminalStatus.Succeeded);
    await batch;
    Assert.Equal(["first", "third", "second"], converter.Started);
  });

  [Fact]
  public Task RetryFailedRunsOnlyFailedBooksAndKeepsTheirEdits() => OnDispatcher(async () =>
  {
    var converter = new KeyedConverter();
    var notifier = new RecordingNotifier();
    using var model = new MainWindowViewModel(new QueueTests.Analyzer(), converter, AppSettings.Defaults with { BooksAtOnce = 2, NotificationsEnabled = true }, notifier: notifier);
    var good = await ReadyItem(model, "good");
    var bad = await ReadyItem(model, "bad");
    var cancelled = await ReadyItem(model, "cancelled");
    var batch = model.ConvertAllAsync();
    await converter.Gate("good").Entered.Task.WaitAsync(Wait);
    await converter.Gate("bad").Entered.Task.WaitAsync(Wait);
    cancelled.Cancel();
    converter.Gate("good").Finish(ConversionTerminalStatus.Succeeded);
    converter.Gate("bad").Finish(ConversionTerminalStatus.ExecutionFailed);
    await batch;
    Assert.Equal(BookOutcome.Success, good.Outcome);
    Assert.Equal(BookOutcome.Failure, bad.Outcome);
    Assert.Equal(BookOutcome.Cancelled, cancelled.Outcome);

    var edited = Path.GetFullPath("retry-destination");
    bad.OutputDirectory = edited;
    var retry = model.RetryFailedAsync();
    var second = converter.Gate("bad", attempt: 2);
    await second.Entered.Task.WaitAsync(Wait);
    Assert.Equal(edited, second.Request!.Options.DestinationDirectory);
    second.Finish(ConversionTerminalStatus.Succeeded);
    await retry;
    Assert.Equal(1, converter.Started.Count(name => name == "good"));
    Assert.Equal(2, converter.Started.Count(name => name == "bad"));
    Assert.DoesNotContain("cancelled", converter.Started);
    Assert.Equal(BookOutcome.Success, bad.Outcome);

    model.ClearCompleted();
    Assert.Equal(["cancelled"], model.Items.Select(item => item.Name));
  });

  private static async Task<BookQueueItemViewModel> ReadyItem(MainWindowViewModel model, string name)
  {
    await model.AddAsync(Path.GetFullPath(name));
    var item = model.Items[^1];
    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);
    Assert.True(item.CanConvert);
    return item;
  }

  private static async Task WaitUntil(Func<bool> condition)
  {
    var deadline = DateTime.UtcNow + Wait;
    while (!condition())
    {
      if (DateTime.UtcNow > deadline) throw new TimeoutException("The queue did not reach the expected state.");
      await Task.Delay(10);
    }
  }

  private static void InterlockedMax(ref int target, int value)
  {
    int current;
    while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
  }

  private static Task OnDispatcher(Func<Task> test)
  {
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
      var dispatcher = Dispatcher.CurrentDispatcher;
      SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
      _ = dispatcher.BeginInvoke(async () =>
      {
        try { await test().WaitAsync(TimeSpan.FromSeconds(60)); completion.SetResult(); }
        catch (Exception exception) { completion.SetException(exception); }
        finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
      });
      Dispatcher.Run();
    })
    { IsBackground = true };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
  }

  private sealed class BookGate
  {
    private readonly TaskCompletionSource<ConversionTerminalStatus> _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConversionRequest? Request { get; set; }
    public Task<ConversionTerminalStatus> Finished => _finish.Task;
    public void Finish(ConversionTerminalStatus status) => _finish.TrySetResult(status);
  }

  // Each book's attempt waits on its own gate, so tests decide exactly when each one finishes.
  private sealed class KeyedConverter : IConversionService
  {
    private readonly ConcurrentDictionary<string, BookGate> _gates = new();
    private readonly ConcurrentDictionary<string, int> _attempts = new();
    private readonly ConcurrentQueue<string> _started = new();
    public string[] Started => [.. _started];
    public BookGate Gate(string name, int attempt = 1) => _gates.GetOrAdd($"{name}#{attempt}", _ => new());

    public async Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      var name = Path.GetFileName(request.Source);
      _started.Enqueue(name);
      var gate = Gate(name, _attempts.AddOrUpdate(name, 1, (_, count) => count + 1));
      gate.Request = request;
      gate.Entered.TrySetResult();
      var status = await gate.Finished.WaitAsync(cancellationToken);
      return new ConversionServiceResult(Guid.NewGuid(), status, ConversionStage.Completed,
        null, null, null, null, null, [], [], status == ConversionTerminalStatus.Succeeded ? "finished.m4b" : null);
    }
  }

  private sealed class RecordingNotifier : IConversionNotifier
  {
    public ConcurrentQueue<NotificationRequest> Shown { get; } = new();
    public void Show(NotificationRequest request) => Shown.Enqueue(request);
  }
}
