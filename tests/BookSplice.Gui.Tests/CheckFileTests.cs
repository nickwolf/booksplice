using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Settings;
using BookSplice.Core.Validation;
using BookSplice.Gui.Bootstrap;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class CheckFileTests
{
  private static readonly string BookPath = Path.GetFullPath("checked-book.m4b");

  [Fact]
  public void AnInspectOnlyBookCanBeCheckedButNotConverted() => DispatcherTestHost.Run(async _ =>
  {
    using var model = Model(new FakeChecker(Report()), new FakeLog());
    var item = await Add(model);
    Assert.Equal("M4B inspection only", item.Status);
    Assert.True(item.CanCheck);
    Assert.False(item.CanConvert);
    Assert.True(item.IsInspectOnly);
  });

  [Fact]
  public void AReadyBookHasNoCheckButton() => DispatcherTestHost.Run(async _ =>
  {
    using var model = new MainWindowViewModel(new QueueTests.Analyzer(), new Idle(), AppSettings.Defaults, checker: new FakeChecker(Report()), checkLog: new FakeLog());
    await model.AddAsync(Path.GetFullPath("ready-book"));
    var item = model.Items[^1];
    await model.ResolveOrderAsync(item, OrderCandidateId.NaturalPath);
    Assert.True(item.CanConvert);
    Assert.False(item.CanCheck);
    Assert.False(item.IsInspectOnly);
    await model.CheckAsync(item);
    Assert.Equal("Ready", item.Status);
  });

  [Fact]
  public void APassingCheckShowsTheLinesInOrderAndWritesAHistoryRecord() => DispatcherTestHost.Run(async _ =>
  {
    var checker = new FakeChecker(Report(
      [new("container.read", ExistingCheckStatus.Passed, "The file opens."), new("audio.decode", ExistingCheckStatus.NotChecked, "Audio was not decoded.", false), new("tags.read", ExistingCheckStatus.Note, "No cover found.", false)],
      ["Chapter timing was not verified."], ["Odd tag"]));
    var log = new FakeLog();
    using var model = Model(checker, log);
    var item = await Add(model);

    await model.CheckAsync(item);

    Assert.Equal("Checked", item.Status);
    Assert.False(item.IsBusy);
    Assert.True(item.CanCheck);
    Assert.False(item.CanConvert);
    Assert.True(item.HasCheckResult);
    Assert.False(item.HasConversionResult);
    Assert.Equal("Check passed", item.Result!.Title);
    Assert.Equal(["Passed: The file opens.", "Not checked: Audio was not decoded.", "Note: No cover found.", "Chapter timing was not verified.", "Odd tag"], item.Result.CheckLines);
    Assert.Equal(BookPath, checker.Paths.Single());
    var record = Assert.Single(log.Records);
    Assert.Equal("check", record.Kind);
    Assert.Equal(BookPath, record.CheckedPath);
    Assert.Equal(ConversionTerminalStatus.Succeeded, record.TerminalStatus);
    Assert.Null(record.PublishedPath);
    Assert.Equal("Lightweight", record.CheckLevel);
    Assert.Equal(["check.container.read", "check.audio.decode", "check.tags.read"], record.Diagnostics.Select(value => value.Code));
    Assert.All(record.Diagnostics, value => Assert.Equal(ServiceDiagnosticSeverity.Information, value.Severity));
    Assert.Equal("The file opens.", record.Diagnostics[0].Message);
  });

  [Fact]
  public void AFailingCheckIsReportedAndLoggedAsValidationFailed() => DispatcherTestHost.Run(async _ =>
  {
    var log = new FakeLog();
    using var model = Model(new FakeChecker(Report([new("audio.codec", ExistingCheckStatus.Failed, "The audio is not AAC."), new("tags.read", ExistingCheckStatus.Failed, "Tags are odd.", false)])), log);
    var item = await Add(model);

    await model.CheckAsync(item);

    Assert.Equal("Check failed", item.Status);
    Assert.Equal("Check failed", item.QueueStatus);
    Assert.Equal(["Failed: The audio is not AAC.", "Failed: Tags are odd."], item.Result!.CheckLines);
    var record = Assert.Single(log.Records);
    Assert.Equal(ConversionTerminalStatus.ValidationFailed, record.TerminalStatus);
    Assert.Equal([ServiceDiagnosticSeverity.Error, ServiceDiagnosticSeverity.Information], record.Diagnostics.Select(value => value.Severity));
    Assert.True(item.CanCheck);
  });

  [Fact]
  public void TheTickDecidesWhetherEveryFrameIsDecodedAndACheckCanRunAgain() => DispatcherTestHost.Run(async _ =>
  {
    var checker = new FakeChecker(Report());
    var log = new FakeLog();
    using var model = Model(checker, log);
    var item = await Add(model);
    Assert.False(item.DecodeAllAudio);
    await model.CheckAsync(item);
    item.DecodeAllAudio = true;
    await model.CheckAsync(item);
    Assert.Equal([false, true], checker.FullDecode);
    Assert.Equal(["Lightweight", "Full decode"], log.Records.Select(record => record.CheckLevel));
    Assert.Equal("Checked", item.Status);
  });

  [Fact]
  public void TheBookIsBusyWhileCheckingAndCancelReturnsItToInspectOnly() => DispatcherTestHost.Run(async dispatcher =>
  {
    var checker = new FakeChecker(Report()) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
    var log = new FakeLog();
    using var model = Model(checker, log);
    var item = await Add(model);

    var running = model.CheckAsync(item);
    await checker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.True(item.IsBusy);
    Assert.False(item.CanCheck);
    Assert.False(item.CanRemove);
    Assert.True(item.CanCancel);
    Assert.Equal("Checking", item.Status);
    checker.Progress!.Report(new ConversionProgress(null, null, null, 0, Phase: ConversionPhase.FullDecode, PhaseOccurrenceId: Guid.NewGuid(), IsPhaseEntry: true, Sequence: 1));
    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    Assert.Contains("Validating full audio", item.Details, StringComparison.Ordinal);

    item.Cancel();
    Assert.True(checker.Token.IsCancellationRequested);
    await running;

    Assert.Equal("Check cancelled", item.Status);
    Assert.False(item.IsBusy);
    Assert.True(item.CanCheck);
    Assert.False(item.HasResult);
    Assert.True(item.IsInspectOnly);
    var record = Assert.Single(log.Records);
    Assert.Equal(ConversionTerminalStatus.Cancelled, record.TerminalStatus);
    Assert.Equal("check", record.Kind);
    Assert.Equal(BookPath, record.CheckedPath);
  });

  [Fact]
  public void AnInspectOnlyBookShowsCheckFileAndDecodeAllAudioInsteadOfConvert() => DispatcherTestHost.Run(async _ =>
  {
    var root = Directory.CreateTempSubdirectory("booksplice-check-").FullName;
    try
    {
      var services = new GuiServices(new InspectOnlyAnalyzer(), new BookHeaderTests.FakePlanner(), new Idle(), new NoCovers(), null, new FakeChecker(Report()), new FakeLog());
      var window = new MainWindow(new JsonSettingsStore(root), AppSettings.Defaults with { OutputDirectory = Path.Combine(root, "out") }, services)
      { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, Width = 1120, Height = 760 };
      window.Show();
      try
      {
        var model = (MainWindowViewModel)window.DataContext;
        var item = await Add(model);
        ((ListBox)window.FindName("Queue")).SelectedItem = item;
        Settle(window);
        var check = (Button)window.FindName("CheckSelectedButton");
        var decode = (CheckBox)window.FindName("DecodeAllAudioBox");
        var convert = (Button)window.FindName("ConvertSelectedButton");
        Assert.Equal(Visibility.Visible, check.Visibility);
        Assert.Equal("Check file (_x)", check.Content);
        Assert.Equal(Visibility.Visible, decode.Visibility);
        Assert.True(decode.IsEnabled);
        Assert.False(decode.IsChecked);
        Assert.Equal(Visibility.Collapsed, convert.Visibility);
        Assert.Equal(Visibility.Collapsed, ((Border)window.FindName("CheckResultCard")).Visibility);

        decode.IsChecked = true;
        Assert.True(item.DecodeAllAudio);
        await model.CheckAsync(item);
        Settle(window);
        Assert.Equal(Visibility.Visible, ((Border)window.FindName("CheckResultCard")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((Border)window.FindName("ConversionResultCard")).Visibility);
        Assert.Equal("Check passed", ((TextBlock)window.FindName("CheckResultTitle")).Text);
        Assert.Equal(Visibility.Visible, check.Visibility);
        Assert.Equal(Visibility.Collapsed, convert.Visibility);
      }
      finally { window.Close(); }
    }
    finally { try { Directory.Delete(root, true); } catch (IOException) { } }
  });

  private static void Settle(Window window)
  {
    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    window.UpdateLayout();
  }

  private static MainWindowViewModel Model(FakeChecker checker, FakeLog log) => new(new InspectOnlyAnalyzer(), new Idle(), AppSettings.Defaults, checker: checker, checkLog: log);

  private static async Task<BookQueueItemViewModel> Add(MainWindowViewModel model)
  {
    await model.AddAsync(BookPath);
    return model.Items[^1];
  }

  private static ExistingFileCheckReport Report(ExistingFileCheck[]? checks = null, string[]? notEstablished = null, string[]? warnings = null)
    => new(BookPath, checks ?? [new("container.read", ExistingCheckStatus.Passed, "The file opens.")], notEstablished ?? [], warnings: warnings);

  private sealed class FakeChecker(ExistingFileCheckReport report) : IExistingFileChecker
  {
    public List<string> Paths { get; } = [];
    public List<bool> FullDecode { get; } = [];
    public TaskCompletionSource? Gate { get; init; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IProgress<ConversionProgress>? Progress { get; private set; }
    public CancellationToken Token { get; private set; }

    public async Task<ExistingFileCheckReport> CheckAsync(string path, bool fullDecode, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      Paths.Add(path);
      FullDecode.Add(fullDecode);
      Progress = progress;
      Token = cancellationToken;
      Entered.TrySetResult();
      if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
      return report;
    }
  }

  private sealed class FakeLog : IJobLogWriter
  {
    public List<ConversionAuditRecord> Records { get; } = [];
    public Task WriteAsync(ConversionAuditRecord record, CancellationToken cancellationToken)
    {
      Records.Add(record);
      return Task.CompletedTask;
    }
  }

  private sealed class InspectOnlyAnalyzer : IBookAnalyzer
  {
    public Task<BookAnalysis> AnalyzeAsync(SourceSelection selection, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
      => AnalyzeAsync(selection.RootPath, chaptersEnabled, options, cancellationToken);

    public Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
      => Task.FromResult(new BookAnalysis(BookAnalysisStatus.InspectOnly, inputPath, [], null,
        new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()),
        new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([], 0), []));
  }

  private sealed class NoCovers : ICoverPayloadOpener
  {
    public ValueTask<Stream> OpenReadAsync(CoverPayloadReference payload, CancellationToken cancellationToken) => throw new InvalidOperationException("No cover should be read.");
  }

  private sealed class Idle : IConversionService
  {
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null) => throw new InvalidOperationException("No conversion should run.");
  }
}
