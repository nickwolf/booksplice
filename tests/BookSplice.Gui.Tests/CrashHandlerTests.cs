using System.Windows;
using BookSplice.Gui.Diagnostics;

namespace BookSplice.Gui.Tests;

public sealed class CrashHandlerTests
{
  private sealed class FakeSink(bool succeed = true, bool throws = false) : ICrashReportSink
  {
    public List<CrashReport> Reports { get; } = [];
    public bool TryWrite(CrashReport report)
    {
      Reports.Add(report);
      if (throws) throw new IOException("sink failed");
      return succeed;
    }
  }

  private sealed class FakeUi : ICrashUi
  {
    public List<bool> Shown { get; } = [];
    public List<int> ExitCodes { get; } = [];
    public bool ThrowOnShow { get; init; }
    public void ShowFatal(bool reportSaved)
    {
      Shown.Add(reportSaved);
      if (ThrowOnShow) throw new InvalidOperationException("no UI");
    }
    public void Shutdown(int exitCode) => ExitCodes.Add(exitCode);
  }

  private static CrashHandler Create(FakeSink sink, FakeUi ui, CrashLaunchMode mode = CrashLaunchMode.Normal, ActiveJobRegistry? jobs = null) =>
    new(() => sink, ui, jobs ?? new ActiveJobRegistry(), mode, () => "9.9.9");

  [Fact]
  public void NormalModeWritesOneReportShowsOneMessageAndShutsDown()
  {
    var sink = new FakeSink();
    var ui = new FakeUi();
    var jobs = new ActiveJobRegistry();
    var job = Guid.NewGuid();
    jobs.Add(job);

    Create(sink, ui, jobs: jobs).HandleDispatcher(new InvalidOperationException("render"));

    var report = Assert.Single(sink.Reports);
    Assert.Equal("dispatcher", report.Source);
    Assert.Equal("9.9.9", report.AppVersion);
    Assert.Equal([job.ToString("D")], report.JobIds);
    Assert.Equal([true], ui.Shown);
    Assert.Equal([1], ui.ExitCodes);
  }

  [Fact]
  public void UncommittedCandidateIsSilentAndExitsNonzero()
  {
    var sink = new FakeSink();
    var ui = new FakeUi();
    var handler = Create(sink, ui, CrashLaunchMode.UpdateCandidate);

    handler.HandleDispatcher(new InvalidDataException("early"));

    Assert.Equal("update-candidate", Assert.Single(sink.Reports).LaunchMode);
    Assert.Empty(ui.Shown);
    Assert.Equal([1], ui.ExitCodes);
  }

  [Fact]
  public void CommittedCandidateBehavesLikeANormalLaunch()
  {
    var sink = new FakeSink();
    var ui = new FakeUi();
    var handler = Create(sink, ui, CrashLaunchMode.UpdateCandidate);
    handler.MarkUpdateCommitted();

    handler.HandleDispatcher(new InvalidDataException("late"));

    Assert.Equal([true], ui.Shown);
    Assert.Equal([1], ui.ExitCodes);
  }

  [Fact]
  public void SecondExceptionIsIgnored()
  {
    var sink = new FakeSink();
    var ui = new FakeUi();
    var handler = Create(sink, ui);

    handler.HandleDispatcher(new InvalidDataException("first"));
    handler.HandleDispatcher(new InvalidDataException("second"));
    handler.HandleAppDomain(new InvalidDataException("third"));
    Assert.False(handler.HandleStartup(new InvalidDataException("fourth")));

    Assert.Single(sink.Reports);
    Assert.Single(ui.Shown);
    Assert.Single(ui.ExitCodes);
  }

  [Fact]
  public void FailingWriterStillShutsDownAndSaysTheReportWasNotSaved()
  {
    var ui = new FakeUi();
    Create(new FakeSink(succeed: false), ui).HandleDispatcher(new InvalidDataException("x"));
    Assert.Equal([false], ui.Shown);
    Assert.Equal([1], ui.ExitCodes);
  }

  [Fact]
  public void ThrowingWriterOrUiDoesNotEscapeAndStillShutsDown()
  {
    var ui = new FakeUi { ThrowOnShow = true };
    var exception = Record.Exception(() => Create(new FakeSink(throws: true), ui).HandleDispatcher(new InvalidDataException("x")));
    Assert.Null(exception);
    Assert.Equal([1], ui.ExitCodes);

    Assert.Null(Record.Exception(() => new CrashHandler(() => null, new FakeUi(), new ActiveJobRegistry(),
      CrashLaunchMode.Normal, () => throw new InvalidOperationException("version")).HandleDispatcher(new InvalidDataException("y"))));
  }

  [Fact]
  public void AppDomainFailuresWriteOnlyAndStartupFailuresReportTheResult()
  {
    var sink = new FakeSink();
    var ui = new FakeUi();
    Create(sink, ui).HandleAppDomain("not an exception");
    Assert.Equal("appdomain", Assert.Single(sink.Reports).Source);
    Assert.Empty(ui.Shown);
    Assert.Empty(ui.ExitCodes);

    var startup = new FakeSink();
    Assert.True(Create(startup, ui).HandleStartup(new InvalidDataException("boot")));
    Assert.Equal("startup", Assert.Single(startup.Reports).Source);
  }

  [Fact]
  public void ExitCancelsRegisteredConversionsWaitsForThemAndExitsWithOne()
  {
    var jobs = new ActiveJobRegistry();
    using var cancellation = new CancellationTokenSource();
    using var tracked = jobs.Track(cancellation);
    tracked.SetTask(Task.Run(() => cancellation.Token.WaitHandle.WaitOne()));
    var exits = new List<int>();

    new WpfCrashUi(jobs, TimeSpan.FromSeconds(5), exits.Add).Shutdown(1);

    Assert.True(cancellation.IsCancellationRequested);
    Assert.Equal([1], exits);
  }

  [Fact]
  public void ExitWaitIsBoundedWhenAConversionNeverFinishes()
  {
    var jobs = new ActiveJobRegistry();
    using var cancellation = new CancellationTokenSource();
    using var tracked = jobs.Track(cancellation);
    var never = new TaskCompletionSource();
    tracked.SetTask(never.Task);
    var exits = new List<int>();
    var started = System.Diagnostics.Stopwatch.StartNew();

    new WpfCrashUi(jobs, TimeSpan.FromMilliseconds(300), exits.Add).Shutdown(1);

    Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));
    Assert.True(cancellation.IsCancellationRequested);
    Assert.Equal([1], exits);
  }

  [Fact]
  public void ExitStillHappensWhenACancellationSourceIsAlreadyDisposed()
  {
    var jobs = new ActiveJobRegistry();
    var cancellation = new CancellationTokenSource();
    using var tracked = jobs.Track(cancellation);
    cancellation.Dispose();
    var exits = new List<int>();

    new WpfCrashUi(jobs, TimeSpan.FromMilliseconds(50), exits.Add).Shutdown(1);

    Assert.Equal([1], exits);
  }

  [Fact]
  public void UnregisteredConversionsAreNotCancelled()
  {
    var jobs = new ActiveJobRegistry();
    using var cancellation = new CancellationTokenSource();
    jobs.Track(cancellation).Dispose();

    new WpfCrashUi(jobs, TimeSpan.FromMilliseconds(50), _ => { }).Shutdown(1);

    Assert.False(cancellation.IsCancellationRequested);
  }

  [Fact]
  public void MessagesMatchTheDocumentedWording()
  {
    Assert.Equal("BookSplice hit an unexpected error and must close. A diagnostic report was saved on this computer.", CrashMessages.Saved);
    Assert.Equal("BookSplice hit an unexpected error and must close. A diagnostic report could not be saved.", CrashMessages.NotSaved);
  }
}

[Collection("WPF")]
public sealed class CrashRenderFailureTests
{
  private sealed class RecordingUi : ICrashUi
  {
    public volatile bool ShutdownRequested;
    public int ExitCode;
    public void ShowFatal(bool reportSaved) { }
    public void Shutdown(int exitCode) { ExitCode = exitCode; ShutdownRequested = true; }
  }

  [Fact]
  public void RenderTimeExceptionIsReportedThroughTheDispatcherHandler()
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-crash-" + Guid.NewGuid().ToString("N"));
    try
    {
      var directory = CrashReportStore.DirectoryFor(root);
      var ui = new RecordingUi();
      var handler = new CrashHandler(() => new CrashReportStore(directory), ui, new ActiveJobRegistry(), CrashLaunchMode.Normal, () => "1.0.0");
      DispatcherTestHost.Run(async dispatcher =>
      {
        dispatcher.UnhandledException += (_, args) =>
        {
          args.Handled = true;
          handler.HandleDispatcher(args.Exception);
        };
        var window = new Window { ShowInTaskbar = false, Left = -10000, Top = -10000, Width = 50, Height = 50 };
        window.Loaded += (_, _) => throw new InvalidOperationException("synthetic render failure");
        window.Show();
        for (var wait = 0; wait < 100 && !ui.ShutdownRequested; wait++) await Task.Delay(50);
        window.Close();
      });

      Assert.True(ui.ShutdownRequested);
      Assert.Equal(1, ui.ExitCode);
      var file = Assert.Single(Directory.GetFiles(directory, "*.json"));
      Assert.Contains("synthetic render failure", File.ReadAllText(file));
    }
    finally
    {
      if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
  }
}
