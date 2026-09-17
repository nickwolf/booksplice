using BookSplice.Core.Chapters;
using BookSplice.Core.Analysis;
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

  private sealed class Fixture : IBookAnalyzer, IConversionPlanner, IConversionExecutor, IOutputValidator, IAtomicPublisher, ITemporaryArtifactCleaner, IJobLogWriter
  {
    public readonly List<string> Calls = [];
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
    public ConversionExecutionResult Execution { get; set; }
    public PublicationResult Publication { get; init; }

    public Fixture()
    {
      Execution = new(ExecutionStatus.Succeeded, TemporaryPath, [], []);
      Publication = new(PublicationStatus.Published, PublishedPath);
    }

    public ConversionService Service => new(this, this, this, this, this, this, this, () => DateTimeOffset.UnixEpoch);

    public ConversionRequest Request(bool dryRun = false) => new("book", true, new ConversionOptions(AppSettings.Defaults with { OutputDirectory = Path.GetDirectoryName(PublishedPath)! }, QualityProfileCatalog.Version1[0]), dryRun);

    public Task<BookAnalysis> AnalyzeAsync(string inputPath, bool chaptersEnabled, BookAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
      Calls.Add("analyze");
      if (AnalysisException is not null) throw AnalysisException;
      return Task.FromResult(new BookAnalysis(AnalysisStatus, inputPath, "book", [], null, Metadata(), new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([], 0), []));
    }

    public Task<ConversionPlanningResult> CreateAsync(BookAnalysis analysis, ConversionOptions options, CancellationToken cancellationToken)
    {
      Calls.Add("plan");
      return Task.FromResult(new ConversionPlanningResult(PlanningStatus, PlanningStatus == BookAnalysisStatus.Ready ? Plan() : null, []));
    }

    public Task<ConversionExecutionResult> ExecuteAsync(ConversionPlan plan, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      Calls.Add("execute");
      return Task.FromResult(Execution);
    }

    public Task<ValidationReport> ValidateAsync(ConversionPlan plan, string temporaryOutputPath, CancellationToken cancellationToken)
    {
      Calls.Add("validate");
      return Task.FromResult(new ValidationReport(temporaryOutputPath, ValidationIsValid, [], errors: ValidationIsValid ? [] : ["invalid"], planId: plan.PlanId));
    }

    public Task<PublicationResult> PublishAsync(ConversionPlan plan, ValidationReport report, CancellationToken cancellationToken)
    {
      Calls.Add("publish");
      return Task.FromResult(Publication);
    }

    public Task CleanupAsync(string temporaryOutputPath, CancellationToken cancellationToken)
    {
      Calls.Add("cleanup");
      CleanupPaths.Add(temporaryOutputPath);
      CleanupTokens.Add(cancellationToken);
      return Task.CompletedTask;
    }

    public Task WriteAsync(ConversionAuditRecord record, CancellationToken cancellationToken)
    {
      Calls.Add("audit");
      Audits.Add(record);
      AuditTokens.Add(cancellationToken);
      return AuditException is null ? Task.CompletedTask : Task.FromException(AuditException);
    }

    private ConversionPlan Plan() => new([], Metadata(), null, [], QualityProfileCatalog.Version1[0], ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, PublishedPath, AudioStrategy.DirectTranscode, [], new(0, 0, 0, long.MaxValue, 0), 1, "test", "GenericMp4");
    private static BookMetadata Metadata() => new(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
  }
}
