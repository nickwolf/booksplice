using System.Text.Json;
using BookSplice.Gui.Diagnostics;

namespace BookSplice.Gui.Tests;

public sealed class CrashReportTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(), "booksplice-crash-" + Guid.NewGuid().ToString("N"));
  private string ReportDirectory => Path.Combine(_root, CrashReportStore.DirectoryName);

  public void Dispose()
  {
    if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
  }

  private static Exception Thrown(Exception exception)
  {
    try { throw exception; }
    catch (Exception caught) { return caught; }
  }

  private static CrashReport Report(Exception exception, DateTimeOffset? at = null, params Guid[] jobs) =>
    CrashReport.Create(exception, CrashSource.Dispatcher, CrashLaunchMode.Normal, "1.2.3", jobs, at ?? DateTimeOffset.UtcNow);

  [Fact]
  public void WritesReadableJsonWithTheExpectedFields()
  {
    var job = Guid.NewGuid();
    var exception = Thrown(new InvalidOperationException("outer", Thrown(new IOException("inner"))));
    var store = new CrashReportStore(ReportDirectory);

    Assert.True(store.TryWrite(Report(exception, null, job)));

    var file = Assert.Single(Directory.GetFiles(ReportDirectory));
    Assert.Matches(@"crash-\d{8}T\d{9}Z-[0-9a-f]{8}\.json$", file);
    using var json = JsonDocument.Parse(File.ReadAllBytes(file));
    var top = json.RootElement;
    Assert.Equal(1, top.GetProperty("schemaVersion").GetInt32());
    Assert.Equal("1.2.3", top.GetProperty("appVersion").GetString());
    Assert.Equal("normal", top.GetProperty("launchMode").GetString());
    Assert.Equal("dispatcher", top.GetProperty("source").GetString());
    Assert.Equal(job.ToString("D"), Assert.Single(top.GetProperty("jobIds").EnumerateArray()).GetString());
    Assert.Equal("System.InvalidOperationException", top.GetProperty("exceptionType").GetString());
    Assert.Equal("outer", top.GetProperty("message").GetString());
    Assert.Contains(nameof(Thrown), top.GetProperty("stackTrace").GetString());
    var inner = Assert.Single(top.GetProperty("innerExceptions").EnumerateArray());
    Assert.Equal("System.IO.IOException", inner.GetProperty("type").GetString());
    Assert.Equal(1, inner.GetProperty("depth").GetInt32());
    Assert.False(top.GetProperty("truncated").GetBoolean());
  }

  [Fact]
  public void WritesWhileTheInstanceLeaseHoldsTheDataRoot()
  {
    var dataRoot = Path.Combine(_root, "BookSplice");
    Directory.CreateDirectory(dataRoot);
    using var lease = BookSplice.Updates.Windows.GuiInstanceLease.TryAcquire(dataRoot);
    Assert.NotNull(lease);

    Assert.True(new CrashReportStore(CrashReportStore.DirectoryFor(dataRoot)).TryWrite(Report(new InvalidDataException("leased"))));
    Assert.Single(Directory.GetFiles(CrashReportStore.DirectoryFor(dataRoot)));
  }

  [Fact]
  public void MissingVersionIsRecordedAsUnknown()
  {
    var report = CrashReport.Create(new InvalidDataException("x"), CrashSource.Startup, CrashLaunchMode.UpdateCandidate, null, [], DateTimeOffset.UtcNow);
    Assert.Equal("unknown", report.AppVersion);
    Assert.Equal("update-candidate", report.LaunchMode);
    Assert.Equal("startup", report.Source);
  }

  [Fact]
  public void InnerExceptionsAreBoundedAndAggregatesFlattened()
  {
    Exception chain = new InvalidDataException("level 9");
    for (var level = 8; level >= 0; level--) chain = new InvalidDataException("level " + level, chain);
    var bounded = CrashReport.Create(chain, CrashSource.Dispatcher, CrashLaunchMode.Normal, "1", [], DateTimeOffset.UtcNow);
    Assert.Equal(CrashReport.MaxExceptionDepth - 1, bounded.InnerExceptions.Count);
    Assert.True(bounded.Truncated);

    var aggregate = new AggregateException(new IOException("a"), new TimeoutException("b"));
    var flat = CrashReport.Create(aggregate, CrashSource.Dispatcher, CrashLaunchMode.Normal, "1", [], DateTimeOffset.UtcNow);
    Assert.Equal(["System.IO.IOException", "System.TimeoutException"], flat.InnerExceptions.Select(item => item.Type));
    Assert.False(flat.Truncated);
  }

  [Fact]
  public void OversizedReportsStayUnderTheCapAndAreMarked()
  {
    var huge = new string('x', 200_000);
    var exception = new InvalidDataException(huge, new InvalidDataException(huge));
    var store = new CrashReportStore(ReportDirectory);

    Assert.True(store.TryWrite(Report(exception)));

    var file = Assert.Single(Directory.GetFiles(ReportDirectory));
    Assert.True(new FileInfo(file).Length <= CrashReportStore.MaxReportBytes);
    using var json = JsonDocument.Parse(File.ReadAllBytes(file));
    Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
    Assert.Contains("[truncated]", json.RootElement.GetProperty("message").GetString());
  }

  [Fact]
  public void KeepsOnlyTheNewestFiveReports()
  {
    var clock = new StepClock();
    var store = new CrashReportStore(ReportDirectory, clock: clock);
    for (var index = 0; index < 8; index++) Assert.True(store.TryWrite(Report(new InvalidDataException("crash " + index))));
    var kept = store.ListUnreviewed();
    Assert.Equal(CrashReportStore.RetainedReports, kept.Count);
    Assert.Equal(["crash 7", "crash 6", "crash 5", "crash 4", "crash 3"], kept.Select(entry => entry.Report.Message));
    Assert.Equal(CrashReportStore.RetainedReports, Directory.GetFiles(ReportDirectory).Length);
  }

  [Fact]
  public void WriteToAnUnusableDirectoryFailsWithoutThrowing()
  {
    Directory.CreateDirectory(_root);
    File.WriteAllText(ReportDirectory, "a file where the folder should be");
    var store = new CrashReportStore(ReportDirectory);

    Assert.False(store.TryWrite(Report(new InvalidDataException("x"))));
    Assert.Empty(store.ListUnreviewed());
  }

  [Theory]
  [InlineData(FaultPoint.CreateDirectory)]
  [InlineData(FaultPoint.Write)]
  [InlineData(FaultPoint.Move)]
  public void InjectedFileFaultsFailQuietlyAndLeaveNoPartialFile(FaultPoint point)
  {
    var store = new CrashReportStore(ReportDirectory, new FaultyFiles(point));

    Assert.False(store.TryWrite(Report(new InvalidDataException("x"))));

    Assert.Empty(Directory.Exists(ReportDirectory) ? Directory.GetFiles(ReportDirectory) : []);
  }

  [Fact]
  public void FailedPruningDoesNotFailTheWrite()
  {
    var store = new CrashReportStore(ReportDirectory, new FaultyFiles(FaultPoint.Delete), new StepClock());
    for (var index = 0; index < 7; index++) Assert.True(store.TryWrite(Report(new InvalidDataException("crash " + index))));
  }

  [Fact]
  public void ReviewedAndCorruptReportsAreNotOffered()
  {
    var store = new CrashReportStore(ReportDirectory, clock: new StepClock());
    store.TryWrite(Report(new InvalidDataException("first")));
    store.TryWrite(Report(new InvalidDataException("second")));
    File.WriteAllText(Path.Combine(ReportDirectory, "crash-99990101T000000000Z-deadbeef.json"), "{ not json");
    File.WriteAllText(Path.Combine(ReportDirectory, "crash-99990102T000000000Z-deadbeef.json"), "{\"schemaVersion\":7}");

    var listed = store.ListUnreviewed();
    Assert.Equal(["second", "first"], listed.Select(entry => entry.Report.Message));

    store.MarkReviewed([listed[0]]);
    Assert.Equal(["first"], store.ListUnreviewed().Select(entry => entry.Report.Message));
  }

  [Fact]
  public void SummaryRemovesPathsAndCredentialsButKeepsStackFrames()
  {
    var profile = @"C:\Users\someone";
    var dataRoot = Path.Combine(profile, "AppData", "Local", "BookSplice");
    var message = $"Failed reading {profile}\\Books\\My Book\\part1.mp3 and \\\\nas\\share\\book.m4b with password=hunter2 and Authorization: Bearer abcdef0123456789abcdef " +
      "token ghp_abcdefghijklmnopqrstuvwxyz0123456789 key AbCdEfGhIjKlMnOpQrStUvWxYz0123456789AbCdEfGh12 and settings in " + dataRoot;
    var stack = $"   at BookSplice.Gui.Fake.Render() in {profile}\\src\\Fake.cs:line 12\r\n   at BookSplice.Gui.Fake.Run()";
    var report = new CrashReport
    {
      CreatedUtc = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero),
      AppVersion = "0.2.0",
      Source = "dispatcher",
      JobIds = ["11111111-2222-3333-4444-555555555555"],
      ExceptionType = "System.InvalidOperationException",
      Message = message,
      StackTrace = stack,
      InnerExceptions = [new CrashExceptionInfo { Depth = 1, Type = "System.IO.IOException", Message = "api_key: sk-secretvalue in " + dataRoot, StackTrace = "   at Inner.Frame()" }]
    };

    var summary = CrashSummaryBuilder.Build([new CrashReportEntry("x", report)], [dataRoot, profile]);

    foreach (var leaked in new[] { "someone", "hunter2", "abcdef0123456789abcdef", "ghp_", "AbCdEfGhIjKlMn", "sk-secretvalue", "nas", "part1.mp3", "BookSplice\\" })
      Assert.DoesNotContain(leaked, summary);
    Assert.Contains("BookSplice.Gui.Fake.Render()", summary);
    Assert.Contains("BookSplice.Gui.Fake.Run()", summary);
    Assert.Contains("Inner.Frame()", summary);
    Assert.Contains("Version: 0.2.0", summary);
    Assert.Contains("11111111-2222-3333-4444-555555555555", summary);
    Assert.Contains("System.IO.IOException", summary);
    Assert.Contains("[redacted]", summary);
  }

  [Fact]
  public void PlainStackTextIsNotRedacted()
  {
    var line = "at Namespace.TypeName`1.MethodName(String argument) in Program.cs:line 42";
    Assert.Equal(line, BookSplice.Core.Analysis.PublicTextRedactor.RedactCredentials(line));
  }

  [Fact]
  public void NoticeIsOfferedOnlyForUnreviewedReportsInNormalMode()
  {
    var store = new CrashReportStore(ReportDirectory);
    Assert.Null(CrashNoticeViewModel.TryCreate(CrashLaunchMode.Normal, store, []));

    store.TryWrite(Report(new InvalidDataException("boom")));
    Assert.Null(CrashNoticeViewModel.TryCreate(CrashLaunchMode.UpdateCandidate, store, []));

    var notice = CrashNoticeViewModel.TryCreate(CrashLaunchMode.Normal, store, []);
    Assert.NotNull(notice);
    Assert.Equal(1, notice.ReportCount);
    Assert.Contains("boom", notice.SummaryText);

    notice.MarkReviewed();
    Assert.Null(CrashNoticeViewModel.TryCreate(CrashLaunchMode.Normal, store, []));
  }

  [Fact]
  public void NoticeSkipsQuietlyWhenReportsCannotBeRead()
  {
    var store = new CrashReportStore(ReportDirectory, new FaultyFiles(FaultPoint.List));
    Assert.Null(CrashNoticeViewModel.TryCreate(CrashLaunchMode.Normal, store, []));
  }

  [Fact]
  public void ActiveJobsAreTrackedAndCleared()
  {
    var registry = new ActiveJobRegistry();
    var first = Guid.NewGuid();
    var second = Guid.NewGuid();
    registry.Add(first);
    registry.Add(second);
    registry.Remove(first);
    Assert.Equal([second], registry.Snapshot());
  }

  public enum FaultPoint { CreateDirectory, Write, Move, Delete, List }

  private sealed class StepClock : TimeProvider
  {
    private DateTimeOffset _now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
  }

  private sealed class FaultyFiles(FaultPoint fault) : ICrashReportFiles
  {
    private readonly CrashReportFiles _real = new();
    public void CreateDirectory(string path)
    {
      if (fault == FaultPoint.CreateDirectory) throw new UnauthorizedAccessException("denied");
      _real.CreateDirectory(path);
    }
    public void WriteNew(string path, byte[] bytes)
    {
      if (fault == FaultPoint.Write)
      {
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        throw new IOException("disk full");
      }
      _real.WriteNew(path, bytes);
    }
    public void Move(string source, string destination)
    {
      if (fault == FaultPoint.Move) throw new IOException("locked");
      _real.Move(source, destination);
    }
    public void Delete(string path)
    {
      if (fault == FaultPoint.Delete && !path.EndsWith(".tmp", StringComparison.Ordinal)) throw new IOException("locked");
      _real.Delete(path);
    }
    public bool Exists(string path) => _real.Exists(path);
    public IReadOnlyList<string> List(string directory, string pattern) =>
      fault == FaultPoint.List ? throw new IOException("denied") : _real.List(directory, pattern);
    public byte[] Read(string path, int maxBytes) => _real.Read(path, maxBytes);
  }
}
