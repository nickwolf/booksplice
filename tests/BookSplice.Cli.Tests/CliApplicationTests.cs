using System.Text.Json;
using BookSplice.Cli;
using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;

namespace BookSplice.Cli.Tests;

public sealed class CliApplicationTests
{
  [Fact]
  public async Task JsonModeWritesOnlyVersionedEventsAndFinalExitCodeToStandardOutput()
  {
    var service = new FakeService(Result(ConversionTerminalStatus.ValidationFailed));
    var output = new StringWriter();
    var error = new StringWriter();
    var application = new CliApplication(new SettingsStore(Settings()), service);

    var exitCode = await application.RunAsync(["book", "--json"], output, error, CancellationToken.None);

    Assert.Equal(CliExitCode.ValidationFailure, exitCode);
    var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    Assert.NotEmpty(lines);
    var events = lines.Select(line => JsonDocument.Parse(line)).ToArray();
    Assert.All(events, item => Assert.Equal(1, item.RootElement.GetProperty("schemaVersion").GetInt32()));
    Assert.All(events, item => Assert.True(item.RootElement.TryGetProperty("event", out _)));
    var final = events[^1].RootElement;
    Assert.Equal("final", final.GetProperty("event").GetString());
    Assert.Equal("ValidationFailed", final.GetProperty("status").GetString());
    Assert.Equal(6, final.GetProperty("exitCode").GetInt32());
    Assert.DoesNotContain("Conversion", output.ToString(), StringComparison.Ordinal);
  }

  [Fact]
  public async Task DryRunJsonIncludesOrderStrategyOutputAndPredicates()
  {
    var plan = Plan();
    var planning = new ConversionPlanningResult(BookAnalysisStatus.Ready, plan, []);
    var result = Result(ConversionTerminalStatus.DryRun) with { Planning = planning };
    var output = new StringWriter();
    var application = new CliApplication(new SettingsStore(Settings()), new FakeService(result));

    var exitCode = await application.RunAsync(["book", "--dry-run", "--json"], output, TextWriter.Null, CancellationToken.None);

    Assert.Equal(CliExitCode.Success, exitCode);
    var finalLine = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[^1];
    using var final = JsonDocument.Parse(finalLine);
    Assert.Equal(["source-1", "source-2"], final.RootElement.GetProperty("plan").GetProperty("sources").EnumerateArray().Select(value => value.GetString()));
    Assert.Equal("DirectTranscode", final.RootElement.GetProperty("plan").GetProperty("strategy").GetString());
    Assert.Equal(plan.OutputPath, final.RootElement.GetProperty("plan").GetProperty("outputPath").GetString());
    Assert.Equal("strategy.test", final.RootElement.GetProperty("plan").GetProperty("predicates")[0].GetString());
  }

  [Fact]
  public async Task JsonModeRedactsSourcePathsFromEventsPlansAndDiagnostics()
  {
    var source = "C:\\Private Books\\Secret Title\\01.mp3";
    var plan = Plan([source]);
    var result = Result(ConversionTerminalStatus.DryRun) with
    {
      Planning = new ConversionPlanningResult(BookAnalysisStatus.Ready, plan, []),
      Diagnostics = [new("test", ServiceDiagnosticSeverity.Warning, $"Unable to read {source}.")],
    };
    var output = new StringWriter();
    var error = new StringWriter();
    var application = new CliApplication(new SettingsStore(Settings()), new FakeService(result));

    await application.RunAsync([source, "--dry-run", "--json"], output, error, CancellationToken.None);

    var publicText = output + error.ToString();
    Assert.DoesNotContain(source, publicText, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Private Books", publicText, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Secret Title", publicText, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task HumanDryRunUsesSourceLabelsAndRedactsDiagnostics()
  {
    var source = "C:\\Private Books\\Secret Title\\01.mp3";
    var plan = Plan([source]);
    var result = Result(ConversionTerminalStatus.DryRun) with
    {
      Planning = new ConversionPlanningResult(BookAnalysisStatus.Ready, plan, []),
      Diagnostics = [new("test", ServiceDiagnosticSeverity.Error, $"Unable to read {source}.")],
    };
    var output = new StringWriter();
    var error = new StringWriter();
    var application = new CliApplication(new SettingsStore(Settings()), new FakeService(result));

    await application.RunAsync([source, "--dry-run"], output, error, CancellationToken.None);

    var publicText = output + error.ToString();
    Assert.Contains("Source 1", output.ToString(), StringComparison.Ordinal);
    Assert.DoesNotContain(source, publicText, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Private Books", publicText, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Secret Title", publicText, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task ParseErrorsReturnUsageCodeWithoutCallingService()
  {
    var service = new FakeService(Result(ConversionTerminalStatus.Succeeded));
    var error = new StringWriter();
    var application = new CliApplication(new SettingsStore(Settings()), service);

    var exitCode = await application.RunAsync(["book", "--unknown"], TextWriter.Null, error, CancellationToken.None);

    Assert.Equal(CliExitCode.UsageError, exitCode);
    Assert.False(service.WasCalled);
    Assert.Contains("Unknown option", error.ToString(), StringComparison.Ordinal);
  }

  [Fact]
  public async Task SettingsLoadFailureWritesFinalJsonAndOneUnexpectedAudit()
  {
    var logs = new RecordingLogs();
    var output = new StringWriter();
    var application = new CliApplication(new ThrowingSettingsStore(), new FakeService(Result(ConversionTerminalStatus.Succeeded)), logs);

    var exitCode = await application.RunAsync(["book", "--json"], output, TextWriter.Null, CancellationToken.None);

    Assert.Equal(CliExitCode.UnexpectedFailure, exitCode);
    using var final = JsonDocument.Parse(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[^1]);
    Assert.Equal("UnexpectedFailure", final.RootElement.GetProperty("status").GetString());
    Assert.Equal(1, final.RootElement.GetProperty("exitCode").GetInt32());
    Assert.Equal(ConversionTerminalStatus.UnexpectedFailure, Assert.Single(logs.Records).TerminalStatus);
  }

  [Fact]
  public async Task SettingsLoadCancellationWritesFinalJsonAndOneCancellationAudit()
  {
    var logs = new RecordingLogs();
    var output = new StringWriter();
    var application = new CliApplication(new CancelledSettingsStore(), new FakeService(Result(ConversionTerminalStatus.Succeeded)), logs);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    var exitCode = await application.RunAsync(["book", "--json"], output, TextWriter.Null, cancellation.Token);

    Assert.Equal(CliExitCode.Cancelled, exitCode);
    using var final = JsonDocument.Parse(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[^1]);
    Assert.Equal("Cancelled", final.RootElement.GetProperty("status").GetString());
    Assert.Equal(8, final.RootElement.GetProperty("exitCode").GetInt32());
    Assert.Equal(ConversionTerminalStatus.Cancelled, Assert.Single(logs.Records).TerminalStatus);
  }

  [Fact]
  public async Task CommandLineOverridesReachTheSharedServiceWithoutSavingSettings()
  {
    var store = new SettingsStore(Settings());
    var service = new FakeService(Result(ConversionTerminalStatus.Succeeded));
    var application = new CliApplication(store, service);

    await application.RunAsync(["book", "--output", "C:\\Override", "--quality", "efficient", "--bitrate", "75", "--jobs", "2", "--no-chapters", "--overwrite"], TextWriter.Null, TextWriter.Null, CancellationToken.None);

    var request = Assert.IsType<ConversionRequest>(service.Request);
    Assert.Equal("C:\\Override", request.Options.DestinationDirectory);
    Assert.Equal("custom", request.Options.QualityProfile.Id);
    Assert.Equal(75, request.Options.QualityProfile.AudioBitrateKbps);
    Assert.Equal(2, request.Options.Settings.ConversionJobs);
    Assert.False(request.CreateChapters);
    Assert.Equal(Core.Naming.CollisionPolicy.Overwrite, request.Options.CollisionPolicy);
    Assert.Equal(0, store.SaveCount);
  }

  [Fact]
  public async Task StopJoinsTimerBeforeFinalAndRejectsLateProgress()
  {
    var service = new LateProgressService(Result(ConversionTerminalStatus.Succeeded));
    var output = new StringWriter();
    var clock = new CliProgressDisplayTests.ManualTimeProvider();
    var application = new CliApplication(new SettingsStore(Settings()), service, timeProvider: clock);

    await application.RunAsync(["book", "--json"], output, TextWriter.Null, CancellationToken.None);
    service.LateProgress!.Report(new(null, null, null, 0, Phase: ConversionPhase.Publication,
      PhaseOccurrenceId: Guid.NewGuid(), IsPhaseEntry: true, JobId: Guid.NewGuid(), Sequence: 3));

    var events = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
      .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
    Assert.Equal("started", events[0].GetProperty("event").GetString());
    Assert.Equal("Analysis", events[1].GetProperty("phase").GetString());
    Assert.Equal("final", events[^1].GetProperty("event").GetString());
    Assert.Equal(3, events.Length);
    Assert.Equal(0, clock.ActiveTimerCount);
    clock.Advance(TimeSpan.FromMinutes(1));
    Assert.Equal(3, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
  }

  [Fact]
  public async Task DecisionPromptSuspendsObservation()
  {
    var service = new DecisionPhaseService();
    var output = new StringWriter();
    var clock = new CliProgressDisplayTests.ManualTimeProvider();
    using var input = new BlockingReader();
    var application = new CliApplication(new SettingsStore(Settings()), service, timeProvider: clock);

    var running = application.RunAsync(["book"], output, TextWriter.Null, CancellationToken.None, input);
    Assert.True(SpinWait.SpinUntil(() => output.ToString().Contains("Select an order number", StringComparison.Ordinal), TimeSpan.FromSeconds(2)));
    Assert.Equal(0, clock.ActiveTimerCount);
    var before = output.ToString();
    clock.Advance(TimeSpan.FromMinutes(1));
    Assert.Equal(before, output.ToString());
    input.Release();
    Assert.Equal(CliExitCode.OrderingDecisionRequired, await running);
  }

  [Fact]
  public async Task SelectedOrderStartsFreshAttempt()
  {
    var service = new DecisionPhaseService();
    var output = new StringWriter();
    var clock = new CliProgressDisplayTests.ManualTimeProvider();
    var application = new CliApplication(new SettingsStore(Settings()), service, timeProvider: clock);

    var exit = await application.RunAsync(["book"], output, TextWriter.Null, CancellationToken.None, new StringReader("1\n"));

    Assert.Equal(CliExitCode.Success, exit);
    Assert.Equal(2, service.Calls);
    Assert.Equal(0, clock.ActiveTimerCount);
    Assert.Equal(2, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
      .Count(line => line.Contains("Analyzing sources", StringComparison.Ordinal)));
  }

  [Fact]
  public async Task ExceptionalAndEarlyTerminalPathsLeaveNoObservationTimer()
  {
    var clock = new CliProgressDisplayTests.ManualTimeProvider();
    var output = new StringWriter();
    var failing = new CliApplication(new SettingsStore(Settings()), new ThrowingService(), timeProvider: clock);
    Assert.Equal(CliExitCode.UnexpectedFailure, await failing.RunAsync(["book", "--json"], output, TextWriter.Null, CancellationToken.None));
    Assert.Equal(0, clock.ActiveTimerCount);
    clock.Advance(TimeSpan.FromMinutes(1));
    Assert.Equal("final", JsonDocument.Parse(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[^1]).RootElement.GetProperty("event").GetString());

    var dryRun = new CliApplication(new SettingsStore(Settings()), new FakeService(Result(ConversionTerminalStatus.DryRun)), timeProvider: clock);
    Assert.Equal(CliExitCode.Success, await dryRun.RunAsync(["book", "--dry-run"], TextWriter.Null, TextWriter.Null, CancellationToken.None));
    Assert.Equal(0, clock.ActiveTimerCount);
    Assert.Equal(CliExitCode.UsageError, await dryRun.RunAsync(["book", "--invalid"], TextWriter.Null, TextWriter.Null, CancellationToken.None));
    Assert.Equal(0, clock.ActiveTimerCount);
  }

  [Fact]
  public async Task CancellationStopsActiveObservationBeforeFinal()
  {
    var service = new CancellableService();
    var clock = new CliProgressDisplayTests.ManualTimeProvider();
    var output = new StringWriter();
    using var cancellation = new CancellationTokenSource();
    var application = new CliApplication(new SettingsStore(Settings()), service, timeProvider: clock);

    var running = application.RunAsync(["book", "--json"], output, TextWriter.Null, cancellation.Token);
    await service.Started.Task;
    clock.Advance(TimeSpan.FromSeconds(30));
    cancellation.Cancel();
    Assert.Equal(CliExitCode.Cancelled, await running);
    Assert.Equal(0, clock.ActiveTimerCount);
    var before = output.ToString();
    clock.Advance(TimeSpan.FromSeconds(30));
    Assert.Equal(before, output.ToString());
    using var final = JsonDocument.Parse(before.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[^1]);
    Assert.Equal("final", final.RootElement.GetProperty("event").GetString());
    Assert.Equal("Cancelled", final.RootElement.GetProperty("status").GetString());
  }

  private static ConversionServiceResult Result(ConversionTerminalStatus status) => new(
    Guid.NewGuid(), status, ConversionStage.Completed, null, null, null, null, null,
    [new("test", ServiceDiagnosticSeverity.Error, "diagnostic")], [], null);

  private static ConversionPlan Plan(IReadOnlyList<string>? sources = null) => new(
    sources ?? ["C:\\Book\\01.mp3", "C:\\Book\\02.mp3"],
    new(new Dictionary<Core.Metadata.SemanticField, Core.Metadata.AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<Core.Metadata.SemanticField, IReadOnlyList<string>>()),
    null, [], QualityProfileCatalog.Version1[0], ValidationLevel.Lightweight, Core.Naming.CollisionPolicy.AvoidCollision,
    "C:\\Output\\Book.m4b", AudioStrategy.DirectTranscode, ["strategy.test"], new(1, 1, 2, 3, 1), 1, "test", Core.Metadata.MetadataProfiles.GenericMp4);

  private static AppSettings Settings() => AppSettings.Defaults with { OutputDirectory = "C:\\Output" };

  private sealed class FakeService(ConversionServiceResult result) : IConversionService
  {
    public bool WasCalled { get; private set; }
    public ConversionRequest? Request { get; private set; }
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      WasCalled = true;
      Request = request;
      progress?.Report(new(500_000, 2.0, "continue", 0, null, 50));
      return Task.FromResult(result);
    }
  }

  private sealed class LateProgressService(ConversionServiceResult result) : IConversionService
  {
    public IProgress<ConversionProgress>? LateProgress { get; private set; }
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      LateProgress = progress;
      progress?.Report(new(null, null, null, 0, Phase: ConversionPhase.Analysis,
        PhaseOccurrenceId: Guid.NewGuid(), IsPhaseEntry: true, JobId: result.JobId, Sequence: 1));
      return Task.FromResult(result);
    }
  }

  private sealed class DecisionPhaseService : IConversionService
  {
    public int Calls { get; private set; }
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      Calls++;
      var job = Guid.NewGuid();
      progress?.Report(new(null, null, null, 0, Phase: ConversionPhase.Analysis,
        PhaseOccurrenceId: Guid.NewGuid(), IsPhaseEntry: true, JobId: job, Sequence: 1));
      if (Calls > 1) return Task.FromResult(Result(ConversionTerminalStatus.Succeeded) with { JobId = job });
      var candidate = new OrderCandidate(OrderCandidateId.NaturalPath, ["01.mp3"], true, OrderConfidence.High, []);
      var analysis = new BookAnalysis(BookAnalysisStatus.NeedsDecision, Path.GetTempPath(), [],
        new OrderResolution(OrderStatus.NeedsDecision, null, [candidate], []),
        new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()),
        new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([], 0), []);
      return Task.FromResult(Result(ConversionTerminalStatus.DecisionRequired) with { JobId = job, Analysis = analysis });
    }
  }

  private sealed class ThrowingService : IConversionService
  {
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      progress?.Report(new(null, null, null, 0, Phase: ConversionPhase.Analysis,
        PhaseOccurrenceId: Guid.NewGuid(), IsPhaseEntry: true, JobId: Guid.NewGuid(), Sequence: 1));
      throw new IOException("injected");
    }
  }

  private sealed class CancellableService : IConversionService
  {
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      progress?.Report(new(null, null, null, 0, Phase: ConversionPhase.Analysis,
        PhaseOccurrenceId: Guid.NewGuid(), IsPhaseEntry: true, JobId: Guid.NewGuid(), Sequence: 1));
      Started.SetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      throw new InvalidOperationException("The service should have been cancelled.");
    }
  }

  private sealed class BlockingReader : TextReader
  {
    private readonly ManualResetEventSlim _release = new();
    public override string? ReadLine() { _release.Wait(); return null; }
    public void Release() => _release.Set();
    protected override void Dispose(bool disposing) { if (disposing) Release(); base.Dispose(disposing); }
  }

  private sealed class SettingsStore(AppSettings settings) : ISettingsStore
  {
    public int SaveCount { get; private set; }
    public string SettingsPath => "settings.json";
    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SettingsLoadResult(SettingsLoadCode.Valid, settings, []));
    public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) { SaveCount++; return Task.CompletedTask; }
  }

  private sealed class ThrowingSettingsStore : ISettingsStore
  {
    public string SettingsPath => "settings.json";
    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromException<SettingsLoadResult>(new IOException("injected"));
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private sealed class CancelledSettingsStore : ISettingsStore
  {
    public string SettingsPath => "settings.json";
    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromCanceled<SettingsLoadResult>(cancellationToken);
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private sealed class RecordingLogs : IJobLogWriter
  {
    public List<ConversionAuditRecord> Records { get; } = [];
    public Task WriteAsync(ConversionAuditRecord record, CancellationToken cancellationToken)
    {
      Records.Add(record);
      return Task.CompletedTask;
    }
  }
}
