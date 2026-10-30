using BookSplice.Core.Chapters;
using BookSplice.Core.Analysis;
using BookSplice.Core.Discovery;
using BookSplice.Core.Tests.Discovery;
using BookSplice.Core.Covers;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.Core.Validation;

namespace BookSplice.Core.Tests.Analysis;

public sealed class ConversionServiceTests
{
  [Fact]
  public async Task ServicePhasesFollowActualOperations()
  {
    var fixture = new Fixture();
    var result = await fixture.Service.ConvertAsync(fixture.Request(), CancellationToken.None, fixture.Progress);

    Assert.Equal(ConversionTerminalStatus.Succeeded, result.Status);
    Assert.Equal([
      "phase:Analysis", "analyze", "phase:Planning", "plan", "execute", "completed", "validate",
      "phase:Publication", "publish", "phase:Cleanup", "cleanup", "phase:AuditWriting", "audit"
    ], fixture.ProgressCalls);
    Assert.DoesNotContain(fixture.Events, value => value.IsPhaseEntry && value.Phase is ConversionPhase.Encoding or ConversionPhase.ValidationChecks);
    Assert.All(fixture.Events, value => Assert.Equal(result.JobId, value.JobId));
    Assert.Equal(Enumerable.Range(1, fixture.Events.Count).Select(value => (long?)value), fixture.Events.Select(value => value.Sequence));
  }

  [Fact]
  public async Task DryRunStopsAtPlanning()
  {
    var fixture = new Fixture();
    await fixture.Service.ConvertAsync(fixture.Request(dryRun: true), CancellationToken.None, fixture.Progress);
    Assert.Equal(["phase:Analysis", "analyze", "phase:Planning", "plan"], fixture.ProgressCalls);
  }

  [Fact]
  public async Task LateExecutorCallbacksAreDiscarded()
  {
    var fixture = new Fixture { EmitLateExecutorCallbackDuringValidation = true };
    await fixture.Service.ConvertAsync(fixture.Request(), CancellationToken.None, fixture.Progress);
    Assert.DoesNotContain("late executor", fixture.ProgressCalls);
    var count = fixture.Events.Count;
    fixture.RetainedExecutorProgress!.Report(new ConversionProgress(null, null, "late", 0));
    Assert.Equal(count, fixture.Events.Count);
    Assert.Equal(count, fixture.Events[^1].Sequence);
  }

  [Fact]
  public async Task ValidationProgressUsesTheServiceEnvelopeAndClosesOnReturn()
  {
    var fixture = new Fixture { EmitValidationProgress = true };
    var result = await fixture.Service.ConvertAsync(fixture.Request(), CancellationToken.None, fixture.Progress);

    var validation = Assert.Single(fixture.Events, value => value.Phase == ConversionPhase.ValidationChecks);
    Assert.Equal(result.JobId, validation.JobId);
    Assert.True(validation.Sequence > fixture.Events.Single(value => value.State == "completed").Sequence);
    var count = fixture.Events.Count;
    fixture.RetainedValidationProgress!.Report(new ConversionProgress(null, null, null, 0, Phase: ConversionPhase.ValidationChecks));
    Assert.Equal(count, fixture.Events.Count);
  }

  [Fact]
  public async Task TerminalReturnClosesAllSinks()
  {
    var fixture = new Fixture { ValidationIsValid = false };
    var result = await fixture.Service.ConvertAsync(fixture.Request(), CancellationToken.None, fixture.Progress);
    Assert.Equal(ConversionTerminalStatus.ValidationFailed, result.Status);
    Assert.Equal([
      "phase:Analysis", "analyze", "phase:Planning", "plan", "execute", "completed", "validate",
      "phase:Cleanup", "cleanup", "phase:AuditWriting", "audit"
    ], fixture.ProgressCalls);
    var count = fixture.Events.Count;
    fixture.RetainedExecutorProgress!.Report(new ConversionProgress(null, null, "late", 0));
    Assert.Equal(count, fixture.Events.Count);
  }

  [Fact]
  public async Task AnalysisFailureAnnouncesOnlyAnalysisAndAudit()
  {
    var fixture = new Fixture { AnalysisStatus = BookAnalysisStatus.Invalid };
    await fixture.Service.ConvertAsync(fixture.Request(), CancellationToken.None, fixture.Progress);
    Assert.Equal(["phase:Analysis", "analyze", "phase:AuditWriting", "audit"], fixture.ProgressCalls);
  }

  [Fact]
  public async Task SuccessfulConversionRunsStagesInContractOrder()
  {
    var fixture = new Fixture();

    var result = await fixture.Service.ConvertAsync(fixture.Request(), CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.Succeeded, result.Status);
    Assert.Equal(["analyze", "plan", "execute", "validate", "publish", "cleanup", "audit"], fixture.Calls);
    Assert.Equal(fixture.PublishedPath, result.PublishedPath);
    Assert.Equal([fixture.TemporaryPath], fixture.CleanupPaths);
    Assert.NotNull(result.Analysis);
    Assert.NotNull(result.Planning);
    Assert.NotNull(result.Execution);
    Assert.NotNull(result.Validation);
    Assert.NotNull(result.Publication);
  }

  [Fact]
  public async Task DryRunStopsAfterPlanningWithoutAuditOrCleanup()
  {
    var fixture = new Fixture();

    var result = await fixture.Service.ConvertAsync(fixture.Request(dryRun: true), CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.DryRun, result.Status);
    Assert.Equal(["analyze", "plan"], fixture.Calls);
    Assert.Null(result.Execution);
    Assert.Null(result.Validation);
    Assert.Null(result.Publication);
  }

  [Fact]
  public async Task AmbiguousOrderStopsBeforePlanningAndIsAudited()
  {
    var fixture = new Fixture { AnalysisStatus = BookAnalysisStatus.NeedsDecision };

    var result = await fixture.Service.ConvertAsync(fixture.Request(), CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.DecisionRequired, result.Status);
    Assert.Equal(["analyze", "audit"], fixture.Calls);
    Assert.Null(result.Planning);
    Assert.Null(fixture.Audits.Single().Execution);
  }

  [Fact]
  public async Task InvalidAnalysisIsAuditedWithoutLaterStageResults()
  {
    var fixture = new Fixture { AnalysisStatus = BookAnalysisStatus.Invalid };

    var result = await fixture.Service.ConvertAsync(fixture.Request(), CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.InvalidInput, result.Status);
    Assert.Equal(["analyze", "audit"], fixture.Calls);
    Assert.Null(Assert.Single(fixture.Audits).Planning);
  }

  [Fact]
  public async Task InvalidPlanningIsAuditedWithoutExecution()
  {
    var fixture = new Fixture { PlanningStatus = BookAnalysisStatus.Invalid };

    var result = await fixture.Service.ConvertAsync(fixture.Request(), CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.InvalidInput, result.Status);
    Assert.Equal(["analyze", "plan", "audit"], fixture.Calls);
    Assert.Null(Assert.Single(fixture.Audits).Execution);
  }

  [Fact]
  public async Task FailedExecutionSkipsValidationAndPublicationAndIsAudited()
  {
    var fixture = new Fixture
    {
      Execution = new(ExecutionStatus.Failed, null, [], [new("execution.failed", "failed")]),
    };

    var result = await fixture.Service.ConvertAsync(fixture.Request(), CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.ExecutionFailed, result.Status);
    Assert.Equal(["analyze", "plan", "execute", "audit"], fixture.Calls);
    Assert.Null(result.Validation);
    Assert.Null(result.Publication);
  }

  [Fact]
  public async Task FailedValidationCleansOnlyTheControlledTemporaryOutput()
  {
    var fixture = new Fixture { ValidationIsValid = false };

    var result = await fixture.Service.ConvertAsync(fixture.Request(), CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.ValidationFailed, result.Status);
    Assert.Equal(["analyze", "plan", "execute", "validate", "cleanup", "audit"], fixture.Calls);
    Assert.Equal([fixture.TemporaryPath], fixture.CleanupPaths);
    Assert.All(fixture.CleanupTokens, token => Assert.False(token.CanBeCanceled));
  }

  [Fact]
  public async Task FailedPublicationCleansTemporaryOutputAndPreservesPublicationResult()
  {
    var fixture = new Fixture
    {
      Publication = new(PublicationStatus.Failed, null, ["publication failed"]),
    };

    var result = await fixture.Service.ConvertAsync(fixture.Request(), CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.PublicationFailed, result.Status);
    Assert.Equal(["analyze", "plan", "execute", "validate", "publish", "cleanup", "audit"], fixture.Calls);
    Assert.Same(fixture.Publication, result.Publication);
  }

  [Fact]
  public async Task CancelledExecutionReturnsCancellationAndUsesUncancelledAuditToken()
  {
    var fixture = new Fixture
    {
      Execution = new(ExecutionStatus.Cancelled, null, [], [new("execution.cancelled", "cancelled")]),
    };
    using var cancellation = new CancellationTokenSource();

    var result = await fixture.Service.ConvertAsync(fixture.Request(), cancellation.Token);

    Assert.Equal(ConversionTerminalStatus.Cancelled, result.Status);
    Assert.Equal(["analyze", "plan", "execute", "audit"], fixture.Calls);
    Assert.All(fixture.AuditTokens, token => Assert.False(token.CanBeCanceled));
  }

  [Fact]
  public async Task CancelledExecutionCleansAReportedControlledTemporaryOutput()
  {
    var fixture = new Fixture();
    fixture.Execution = new(ExecutionStatus.Cancelled, fixture.TemporaryPath, [], [new("execution.cancelled", "cancelled")]);

    var result = await fixture.Service.ConvertAsync(fixture.Request(), CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.Cancelled, result.Status);
    Assert.Equal(["analyze", "plan", "execute", "cleanup", "audit"], fixture.Calls);
    Assert.Equal([fixture.TemporaryPath], fixture.CleanupPaths);
  }

  [Fact]
  public async Task AuditFailureAddsWarningWithoutChangingPublishedSuccess()
  {
    var fixture = new Fixture { AuditException = new IOException("disk full") };

    var result = await fixture.Service.ConvertAsync(fixture.Request(), CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.Succeeded, result.Status);
    Assert.Equal(fixture.PublishedPath, result.PublishedPath);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "audit.write-failed" && diagnostic.Severity == ServiceDiagnosticSeverity.Warning);
  }

  [Fact]
  public async Task UnexpectedFailureIsAuditedWithNullableStageResults()
  {
    var fixture = new Fixture { AnalysisException = new InvalidOperationException("boom") };

    var result = await fixture.Service.ConvertAsync(fixture.Request(), CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.UnexpectedFailure, result.Status);
    Assert.Equal(["analyze", "audit"], fixture.Calls);
    var audit = Assert.Single(fixture.Audits);
    Assert.Null(audit.Analysis);
    Assert.Null(audit.Planning);
    Assert.Null(audit.Execution);
    Assert.Null(audit.Validation);
    Assert.Null(audit.Publication);
  }

  [Fact]
  public async Task ChangedSourceOrderStopsBeforePlanning()
  {
    var fixture = new Fixture();
    var request = fixture.Request() with { ExpectedSourcePaths = ["previously-reviewed.mp3"] };
    var result = await fixture.Service.ConvertAsync(request, CancellationToken.None);
    Assert.Equal(ConversionTerminalStatus.DecisionRequired, result.Status);
    Assert.Equal(["analyze", "audit"], fixture.Calls);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "analysis.sources-changed");
  }

  [Fact]
  public async Task SelectionRequestAnalyzesOnlyTheSelectedFilesAtExecution()
  {
    using var tree = new SelectionTree();
    var selected = new[] { tree.Write("book/Disc1/01.mp3"), tree.Write("book/Disc2/01.mp3") };
    tree.Write("book/Disc1/02.mp3");
    var hashes = selected.Select(SelectionTree.Hash).ToArray();
    var fixture = new Fixture();
    var selection = SourceSelection.Create(selected);

    var result = await fixture.ServiceWith(SelectionTree.Analyzer()).ConvertAsync(fixture.Request() with { Source = selection.RootPath, Selection = selection }, CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.Succeeded, result.Status);
    Assert.Equal(selected.Order(StringComparer.OrdinalIgnoreCase), fixture.PlannedAnalysis!.OrderedFiles.Select(file => file.FullPath).Order(StringComparer.OrdinalIgnoreCase));
    Assert.Equal(hashes, selected.Select(SelectionTree.Hash));
  }

  [Fact]
  public async Task UnchangedReviewedSourcesProceedAndAChangedFileStopsBeforePlanning()
  {
    using var tree = new SelectionTree();
    var selected = new[] { tree.Write("book/01.mp3"), tree.Write("book/02.mp3") };
    var selection = SourceSelection.Create(selected);
    var analyzer = SelectionTree.Analyzer();
    var reviewed = await analyzer.AnalyzeAsync(selection, false);
    var identities = reviewed.OrderedFiles.Select(file => file.Identity!).ToArray();
    var paths = reviewed.OrderedFiles.Select(file => file.FullPath).ToArray();
    var fixture = new Fixture();
    var request = fixture.Request() with { Selection = selection, ExpectedSourcePaths = paths, ExpectedSources = identities };
    Assert.Equal(ConversionTerminalStatus.Succeeded, (await fixture.ServiceWith(analyzer).ConvertAsync(request, CancellationToken.None)).Status);

    File.AppendAllText(selected[1], "edited after review");
    var changed = new Fixture();
    var result = await changed.ServiceWith(analyzer).ConvertAsync(request, CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.DecisionRequired, result.Status);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "analysis.sources-changed");
    Assert.DoesNotContain("plan", changed.Calls);
  }

  [Fact]
  public async Task ATimestampOnlyChangeAfterReviewIsDetected()
  {
    using var tree = new SelectionTree();
    var path = tree.Write("book/01.mp3");
    var selection = SourceSelection.Create([path]);
    var analyzer = SelectionTree.Analyzer();
    var identities = (await analyzer.AnalyzeAsync(selection, false)).OrderedFiles.Select(file => file.Identity!).ToArray();
    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
    var fixture = new Fixture();

    var result = await fixture.ServiceWith(analyzer).ConvertAsync(fixture.Request() with { Selection = selection, ExpectedSources = identities }, CancellationToken.None);

    Assert.Equal(ConversionTerminalStatus.DecisionRequired, result.Status);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "analysis.sources-changed");
  }

  [Fact]
  public async Task ARequestWithASelectionRoutesToTheSelectionAnalyzer()
  {
    var fixture = new Fixture();
    var selection = SourceSelection.Create([Path.Combine(Path.GetTempPath(), "a", "01.mp3"), Path.Combine(Path.GetTempPath(), "b", "01.mp3")]);

    await fixture.Service.ConvertAsync(fixture.Request() with { Selection = selection }, CancellationToken.None);

    Assert.Same(selection, fixture.AnalyzedSelection);
  }

  private sealed class Fixture : IBookAnalyzer, IConversionPlanner, IConversionExecutor, IOutputValidator, IAtomicPublisher, ITemporaryArtifactCleaner, IJobLogWriter
  {
    public readonly List<string> Calls = [];
    public readonly List<string> ProgressCalls = [];
    public readonly List<ConversionProgress> Events = [];
    public IProgress<ConversionProgress>? RetainedExecutorProgress;
    public ConversionPhaseScope? RetainedValidationProgress;
    public IProgress<ConversionProgress> Progress => new ImmediateProgress(this);
    public readonly List<string> CleanupPaths = [];
    public readonly List<CancellationToken> CleanupTokens = [];
    public readonly List<ConversionAuditRecord> Audits = [];
    public readonly List<CancellationToken> AuditTokens = [];
    public readonly string TemporaryPath = Path.Combine(Path.GetTempPath(), "abc-job", "output.m4b");
    public readonly string PublishedPath = Path.Combine(Path.GetTempPath(), "abc-output", "book.m4b");
    public BookAnalysisStatus AnalysisStatus { get; init; } = BookAnalysisStatus.Ready;
    public BookAnalysisStatus PlanningStatus { get; init; } = BookAnalysisStatus.Ready;
    public Exception? AnalysisException { get; init; }
    public Exception? AuditException { get; init; }
    public bool ValidationIsValid { get; init; } = true;
    public bool EmitLateExecutorCallbackDuringValidation { get; init; }
    public bool EmitValidationProgress { get; init; }
    public ConversionExecutionResult Execution { get; set; }
    public PublicationResult Publication { get; init; }

    public Fixture()
    {
      Execution = new(ExecutionStatus.Succeeded, TemporaryPath, [], []);
      Publication = new(PublicationStatus.Published, PublishedPath);
    }

    public BookAnalysis? PlannedAnalysis { get; private set; }
    public ConversionService ServiceWith(IBookAnalyzer analyzer) => new(analyzer, this, this, this, this, this, this, () => DateTimeOffset.UnixEpoch);
    public ConversionService Service => new(this, this, this, this, this, this, this, () => DateTimeOffset.UnixEpoch);

    public ConversionRequest Request(bool dryRun = false) => new("book", true, new ConversionOptions(AppSettings.Defaults with { OutputDirectory = Path.GetDirectoryName(PublishedPath)! }, QualityProfileCatalog.Version1[0]), dryRun);

    public SourceSelection? AnalyzedSelection { get; private set; }
    public Task<BookAnalysis> AnalyzeAsync(SourceSelection selection, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      AnalyzedSelection = selection;
      return AnalyzeAsync(selection.RootPath, chaptersEnabled, options, cancellationToken);
    }

    public Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      Calls.Add("analyze");
      ProgressCalls.Add("analyze");
      if (AnalysisException is not null) throw AnalysisException;
      return Task.FromResult(new BookAnalysis(AnalysisStatus, inputPath, "book", [], null, Metadata(), new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([], 0), []));
    }

    public Task<ConversionPlanningResult> CreateAsync(BookAnalysis analysis, ConversionOptions options, CancellationToken cancellationToken)
    {
      Calls.Add("plan");
      PlannedAnalysis = analysis;
      ProgressCalls.Add("plan");
      return Task.FromResult(new ConversionPlanningResult(PlanningStatus, PlanningStatus == BookAnalysisStatus.Ready ? Plan() : null, []));
    }

    public Task<ConversionExecutionResult> ExecuteAsync(ConversionPlan plan, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      Calls.Add("execute");
      ProgressCalls.Add("execute");
      RetainedExecutorProgress = progress;
      progress?.Report(new ConversionProgress(null, null, "completed", 0));
      return Task.FromResult(Execution);
    }

    public Task<ValidationReport> ValidateAsync(ConversionPlan plan, string temporaryOutputPath, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      Calls.Add("validate");
      ProgressCalls.Add("validate");
      if (EmitValidationProgress)
      {
        var phase = ConversionPhaseScope.Start(progress, ConversionPhase.ValidationChecks);
        RetainedValidationProgress = phase;
        phase.Dispose();
      }
      if (EmitLateExecutorCallbackDuringValidation) RetainedExecutorProgress!.Report(new ConversionProgress(null, null, "late executor", 0));
      return Task.FromResult(new ValidationReport(temporaryOutputPath, ValidationIsValid, [], errors: ValidationIsValid ? [] : ["invalid"], planId: plan.PlanId));
    }

    public Task<PublicationResult> PublishAsync(ConversionPlan plan, ValidationReport report, CancellationToken cancellationToken)
    {
      Calls.Add("publish");
      ProgressCalls.Add("publish");
      return Task.FromResult(Publication);
    }

    public Task CleanupAsync(string temporaryOutputPath, CancellationToken cancellationToken)
    {
      Calls.Add("cleanup");
      ProgressCalls.Add("cleanup");
      CleanupPaths.Add(temporaryOutputPath);
      CleanupTokens.Add(cancellationToken);
      return Task.CompletedTask;
    }

    public Task WriteAsync(ConversionAuditRecord record, CancellationToken cancellationToken)
    {
      Calls.Add("audit");
      ProgressCalls.Add("audit");
      Audits.Add(record);
      AuditTokens.Add(cancellationToken);
      return AuditException is null ? Task.CompletedTask : Task.FromException(AuditException);
    }

    private ConversionPlan Plan() => new([], Metadata(), null, [], QualityProfileCatalog.Version1[0], ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, PublishedPath, AudioStrategy.DirectTranscode, [], new(0, 0, 0, long.MaxValue, 0), 1, "test", Core.Metadata.MetadataProfiles.GenericMp4);
    private static BookMetadata Metadata() => new(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());

    private sealed class ImmediateProgress(Fixture fixture) : IProgress<ConversionProgress>
    {
      public void Report(ConversionProgress value)
      {
        fixture.Events.Add(value);
        fixture.ProgressCalls.Add(value.IsPhaseEntry ? $"phase:{value.Phase}" : value.State ?? "update");
      }
    }
  }
}
