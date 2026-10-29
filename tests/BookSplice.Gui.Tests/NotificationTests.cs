using BookSplice.Core.Settings;
using BookSplice.Gui.Notifications;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class NotificationTests
{
  private static readonly AppSettings On = AppSettings.Defaults with { NotificationsEnabled = true };
  private static BookFinished Book(string name, BookOutcome outcome, string? path = null) => new("key-" + name, name, outcome, path);

  [Fact]
  public void NotificationsAreOffByDefault()
  {
    Assert.False(AppSettings.Defaults.NotificationsEnabled);
    Assert.Null(NotificationPolicy.ForBook(Book("A", BookOutcome.Success), AppSettings.Defaults));
    Assert.Null(NotificationPolicy.ForBatch([Book("A", BookOutcome.Success), Book("B", BookOutcome.Failure)], AppSettings.Defaults));
  }

  [Theory]
  [InlineData(BookOutcome.Success, "succeeded")]
  [InlineData(BookOutcome.Warning, "warnings")]
  [InlineData(BookOutcome.Failure, "failed")]
  [InlineData(BookOutcome.Cancelled, "cancelled")]
  public void EachOutcomeHasItsOwnWording(BookOutcome outcome, string word)
  {
    var request = NotificationPolicy.ForBook(Book("Dune", outcome, @"C:\out\Dune.m4b"), On)!;
    Assert.Contains(word, request.Title);
    Assert.Contains("Dune", request.Body);
    Assert.Equal("key-Dune", request.ResultKey);
  }

  [Fact]
  public void PrivacyOmitsTitlesAndPathsEverywhere()
  {
    var settings = On with { NotificationsHideDetails = true };
    var single = NotificationPolicy.ForBook(Book("Secret Title", BookOutcome.Success, @"C:\Private\Secret Title.m4b"), settings)!;
    var batch = NotificationPolicy.ForBatch([Book("Secret Title", BookOutcome.Failure), Book("Other Book", BookOutcome.Warning), Book("Third", BookOutcome.Success)], settings)!;
    foreach (var text in new[] { single.Title + single.Body, batch.Title + batch.Body })
    {
      Assert.DoesNotContain("Secret", text);
      Assert.DoesNotContain("Other", text);
      Assert.DoesNotContain("Private", text);
    }
  }

  [Fact]
  public void BatchSummaryCountsOutcomesAndNamesProblemBooks()
  {
    var request = NotificationPolicy.ForBatch([Book("A", BookOutcome.Success), Book("B", BookOutcome.Success), Book("C", BookOutcome.Failure), Book("D", BookOutcome.Cancelled)], On)!;
    Assert.Equal("4 conversions finished", request.Title);
    Assert.Contains("2 succeeded", request.Body);
    Assert.Contains("1 failed", request.Body);
    Assert.Contains("1 cancelled", request.Body);
    Assert.Contains("C", request.Body);
    Assert.DoesNotContain("D", request.Body.Replace("1 cancelled", ""));
  }

  [Fact]
  public void EmptyBatchShowsNothingAndABatchOfOneReadsAsABook()
  {
    Assert.Null(NotificationPolicy.ForBatch([], On));
    Assert.Contains("succeeded", NotificationPolicy.ForBatch([Book("A", BookOutcome.Success)], On)!.Title);
  }

  [Fact]
  public void AThrowingNotifierIsSwallowed()
  {
    var notifier = new Recorder { Throw = true };
    var batch = new BatchNotifier(notifier, On);
    batch.Add(Book("A", BookOutcome.Success));
    batch.Complete();
    Assert.Equal(1, notifier.Attempts);
  }

  [Fact]
  public async Task ConvertAllShowsOneSummaryAfterEveryBookAndNoPerBookToasts()
  {
    var notifier = new Recorder();
    using var model = Model(notifier, On with { BooksAtOnce = 2 });
    await Fill(model, 3);
    await model.ConvertAllAsync();
    await model.NotificationsDelivered();
    var request = Assert.Single(notifier.Shown);
    Assert.Equal("3 conversions finished", request.Title);
    Assert.All(model.Items, item => Assert.Equal("Complete", item.Status));
  }

  [Fact]
  public async Task SingleConvertShowsOneBookNotification()
  {
    var notifier = new Recorder();
    using var model = Model(notifier, On);
    await Fill(model, 1);
    await model.ConvertAsync(model.Items[0]);
    await model.NotificationsDelivered();
    var request = Assert.Single(notifier.Shown);
    Assert.Contains("succeeded", request.Title);
  }

  [Fact]
  public async Task DisabledSettingAndFailingNotifierDoNotAffectConversion()
  {
    var quiet = new Recorder();
    using var off = Model(quiet, AppSettings.Defaults);
    await Fill(off, 1);
    await off.ConvertAllAsync();
    await off.NotificationsDelivered();
    Assert.Equal(0, quiet.Attempts);

    var broken = new Recorder { Throw = true };
    using var model = Model(broken, On);
    await Fill(model, 2);
    await model.ConvertAllAsync();
    await model.NotificationsDelivered();
    Assert.Equal(1, broken.Attempts);
    Assert.All(model.Items, item => Assert.Equal("Complete", item.Status));
  }

  [Fact]
  public async Task BookCancelledWhileWaitingForASlotCountsInTheSummary()
  {
    var notifier = new Recorder();
    var gate = new GatedConverter();
    using var model = new MainWindowViewModel(new QueueTests.Analyzer(), gate, On, notifier: notifier);
    await Fill(model, 2);
    var batch = model.ConvertAllAsync();
    var running = await gate.Started.Task;
    var waiting = model.Items.Single(item => item.Source != running);
    while (waiting.Status != "Waiting") await Task.Delay(5);
    waiting.Cancel();
    gate.Release.SetResult();
    await batch;
    await model.NotificationsDelivered();
    var request = Assert.Single(notifier.Shown);
    Assert.Contains("1 succeeded", request.Body);
    Assert.Contains("1 cancelled", request.Body);
  }

  private static MainWindowViewModel Model(IConversionNotifier notifier, AppSettings settings) =>
    new(new QueueTests.Analyzer(), new OkConverter(), settings, notifier: notifier);

  private static async Task Fill(MainWindowViewModel model, int count)
  {
    for (var index = 0; index < count; index++)
    {
      await model.AddAsync(Path.GetFullPath($"book{index}"));
      await model.ResolveOrderAsync(model.Items[index], BookSplice.Core.Ordering.OrderCandidateId.NaturalPath);
    }
  }

  private sealed class OkConverter : BookSplice.Core.Analysis.IConversionService
  {
    public async Task<BookSplice.Core.Analysis.ConversionServiceResult> ConvertAsync(BookSplice.Core.Analysis.ConversionRequest request, CancellationToken cancellationToken, IProgress<BookSplice.Core.Execution.ConversionProgress>? progress = null)
    {
      await Task.Delay(20, cancellationToken);
      return new(Guid.NewGuid(), BookSplice.Core.Analysis.ConversionTerminalStatus.Succeeded, BookSplice.Core.Analysis.ConversionStage.Completed, null, null, null, null, null, [], [], "finished.m4b");
    }
  }

  private sealed class GatedConverter : BookSplice.Core.Analysis.IConversionService
  {
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<BookSplice.Core.Analysis.ConversionServiceResult> ConvertAsync(BookSplice.Core.Analysis.ConversionRequest request, CancellationToken cancellationToken, IProgress<BookSplice.Core.Execution.ConversionProgress>? progress = null)
    {
      _ = Started.TrySetResult(request.Source);
      await Release.Task.WaitAsync(cancellationToken);
      return new(Guid.NewGuid(), BookSplice.Core.Analysis.ConversionTerminalStatus.Succeeded, BookSplice.Core.Analysis.ConversionStage.Completed, null, null, null, null, null, [], [], "finished.m4b");
    }
  }

  private sealed class Recorder : IConversionNotifier
  {
    private readonly List<NotificationRequest> _shown = [];
    private int _attempts;
    public bool Throw { get; init; }
    public int Attempts => Volatile.Read(ref _attempts);
    public void Show(NotificationRequest request)
    {
      lock (_shown) _shown.Add(request);
      Interlocked.Increment(ref _attempts);
      if (Throw) throw new InvalidOperationException("denied");
    }

    public IReadOnlyList<NotificationRequest> Shown { get { lock (_shown) return [.. _shown]; } }
  }
}
