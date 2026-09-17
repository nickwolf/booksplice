using BookSplice.Core.Execution;
using BookSplice.Core.Planning;
using BookSplice.Core.Validation;

namespace BookSplice.Core.Analysis;

public enum ConversionTerminalStatus
{
  Succeeded,
  DryRun,
  InvalidInput,
  DecisionRequired,
  ExecutionFailed,
  ValidationFailed,
  PublicationFailed,
  Cancelled,
  UnexpectedFailure,
}

public enum ConversionStage { Analysis, Planning, Execution, Validation, Publication, Cleanup, Audit, Completed }
public enum ServiceDiagnosticSeverity { Error, Warning, Information }
public sealed record ServiceDiagnostic(string Code, ServiceDiagnosticSeverity Severity, string Message);
public sealed record ServiceStageTiming(ConversionStage Stage, DateTimeOffset StartedAt, DateTimeOffset FinishedAt);
public sealed record ConversionRequest(string Source, bool CreateChapters, ConversionOptions Options, bool DryRun = false, BookAnalysisOptions? AnalysisOptions = null);

public sealed record ConversionServiceResult(
  Guid JobId,
  ConversionTerminalStatus Status,
  ConversionStage Stage,
  BookAnalysis? Analysis,
  ConversionPlanningResult? Planning,
  ConversionExecutionResult? Execution,
  ValidationReport? Validation,
  PublicationResult? Publication,
  IReadOnlyList<ServiceDiagnostic> Diagnostics,
  IReadOnlyList<ServiceStageTiming> Timings,
  string? PublishedPath);

public interface IConversionService
{
  Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null);
}

public sealed class ConversionService : IConversionService
{
  private readonly IBookAnalyzer _analyzer;
  private readonly IConversionPlanner _planner;
  private readonly IConversionExecutor _executor;
  private readonly IOutputValidator _validator;
  private readonly IAtomicPublisher _publisher;
  private readonly ITemporaryArtifactCleaner _cleaner;
  private readonly IJobLogWriter _logs;
  private readonly Func<DateTimeOffset> _clock;

  public ConversionService(IBookAnalyzer analyzer, IConversionPlanner planner, IConversionExecutor executor, IOutputValidator validator, IAtomicPublisher publisher, ITemporaryArtifactCleaner cleaner, IJobLogWriter logs, Func<DateTimeOffset>? clock = null)
    => (_analyzer, _planner, _executor, _validator, _publisher, _cleaner, _logs, _clock) = (analyzer, planner, executor, validator, publisher, cleaner, logs, clock ?? (() => DateTimeOffset.UtcNow));

  public async Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
  {
    ArgumentNullException.ThrowIfNull(request);
    var jobId = Guid.NewGuid();
    var started = _clock();
    var timings = new List<ServiceStageTiming>();
    var diagnostics = new List<ServiceDiagnostic>();
    BookAnalysis? analysis = null;
    ConversionPlanningResult? planning = null;
    ConversionExecutionResult? execution = null;
    ValidationReport? validation = null;
    PublicationResult? publication = null;
    var stage = ConversionStage.Analysis;

    try
    {
      analysis = await TimedAsync(ConversionStage.Analysis, token => _analyzer.AnalyzeAsync(request.Source, request.CreateChapters, request.AnalysisOptions, token), timings, cancellationToken).ConfigureAwait(false);
      AddAnalysisDiagnostics(analysis.Diagnostics, diagnostics);
      if (analysis.Status != BookAnalysisStatus.Ready)
      {
        var status = analysis.Status == BookAnalysisStatus.NeedsDecision ? ConversionTerminalStatus.DecisionRequired : ConversionTerminalStatus.InvalidInput;
        return await FinishAsync(Result(status, ConversionStage.Analysis), request.DryRun, started).ConfigureAwait(false);
      }

      stage = ConversionStage.Planning;
      planning = await TimedAsync(ConversionStage.Planning, token => _planner.CreateAsync(analysis, request.Options, token), timings, cancellationToken).ConfigureAwait(false);
      AddAnalysisDiagnostics(planning.Diagnostics, diagnostics);
      if (planning.Plan is null || planning.Status != BookAnalysisStatus.Ready)
        return await FinishAsync(Result(ConversionTerminalStatus.InvalidInput, ConversionStage.Planning), request.DryRun, started).ConfigureAwait(false);
      if (request.DryRun) return Result(ConversionTerminalStatus.DryRun, ConversionStage.Planning);

      stage = ConversionStage.Execution;
      execution = await TimedAsync(ConversionStage.Execution, token => _executor.ExecuteAsync(planning.Plan, token, progress), timings, cancellationToken).ConfigureAwait(false);
      diagnostics.AddRange(execution.Diagnostics.Select(value => new ServiceDiagnostic(value.Code, value.Code == "execution.cancelled" ? ServiceDiagnosticSeverity.Information : ServiceDiagnosticSeverity.Error, value.Message)));
      if (execution.Status == ExecutionStatus.Cancelled)
      {
        if (!string.IsNullOrWhiteSpace(execution.TemporaryOutputPath)) await CleanupAsync(execution.TemporaryOutputPath, diagnostics, timings).ConfigureAwait(false);
        return await FinishAsync(Result(ConversionTerminalStatus.Cancelled, ConversionStage.Execution), false, started).ConfigureAwait(false);
      }
      if (execution.Status != ExecutionStatus.Succeeded || string.IsNullOrWhiteSpace(execution.TemporaryOutputPath))
        return await FinishAsync(Result(ConversionTerminalStatus.ExecutionFailed, ConversionStage.Execution), false, started).ConfigureAwait(false);

      stage = ConversionStage.Validation;
      validation = await TimedAsync(ConversionStage.Validation, token => _validator.ValidateAsync(planning.Plan, execution.TemporaryOutputPath, token), timings, cancellationToken).ConfigureAwait(false);
      diagnostics.AddRange(validation.Warnings.Select(value => new ServiceDiagnostic("validation.warning", ServiceDiagnosticSeverity.Warning, value)));
      diagnostics.AddRange(validation.Errors.Select(value => new ServiceDiagnostic("validation.failed", ServiceDiagnosticSeverity.Error, value)));
      if (!validation.IsValid)
      {
        await CleanupAsync(execution.TemporaryOutputPath, diagnostics, timings).ConfigureAwait(false);
        return await FinishAsync(Result(ConversionTerminalStatus.ValidationFailed, ConversionStage.Validation), false, started).ConfigureAwait(false);
      }

      stage = ConversionStage.Publication;
      publication = await TimedAsync(ConversionStage.Publication, token => _publisher.PublishAsync(planning.Plan, validation, token), timings, cancellationToken).ConfigureAwait(false);
      diagnostics.AddRange(publication.Diagnostics.Select(value => new ServiceDiagnostic("publication.failed", ServiceDiagnosticSeverity.Error, value)));
      if (publication.Status != PublicationStatus.Published || string.IsNullOrWhiteSpace(publication.FinalPath))
      {
        await CleanupAsync(execution.TemporaryOutputPath, diagnostics, timings).ConfigureAwait(false);
        return await FinishAsync(Result(ConversionTerminalStatus.PublicationFailed, ConversionStage.Publication), false, started).ConfigureAwait(false);
      }

      await CleanupAsync(execution.TemporaryOutputPath, diagnostics, timings).ConfigureAwait(false);
      return await FinishAsync(Result(ConversionTerminalStatus.Succeeded, ConversionStage.Completed), false, started).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      if (!string.IsNullOrWhiteSpace(execution?.TemporaryOutputPath)) await CleanupAsync(execution.TemporaryOutputPath, diagnostics, timings).ConfigureAwait(false);
      diagnostics.Add(new("conversion.cancelled", ServiceDiagnosticSeverity.Information, "The conversion was cancelled."));
      return await FinishAsync(Result(ConversionTerminalStatus.Cancelled, stage), request.DryRun, started).ConfigureAwait(false);
    }
    catch (Exception)
    {
      if (!string.IsNullOrWhiteSpace(execution?.TemporaryOutputPath)) await CleanupAsync(execution.TemporaryOutputPath, diagnostics, timings).ConfigureAwait(false);
      diagnostics.Add(new("conversion.unexpected", ServiceDiagnosticSeverity.Error, "The conversion failed unexpectedly."));
      return await FinishAsync(Result(ConversionTerminalStatus.UnexpectedFailure, stage), request.DryRun, started).ConfigureAwait(false);
    }

    ConversionServiceResult Result(ConversionTerminalStatus status, ConversionStage resultStage) => new(jobId, status, resultStage, analysis, planning, execution, validation, publication, diagnostics.AsReadOnly(), timings.AsReadOnly(), publication?.FinalPath);
  }

  private async Task<ConversionServiceResult> FinishAsync(ConversionServiceResult result, bool dryRun, DateTimeOffset started)
  {
    if (dryRun) return result;
    var analysis = result.Analysis;
    var plan = result.Planning?.Plan;
    var record = new ConversionAuditRecord(
      ConversionAuditRecord.CurrentSchemaVersion, result.JobId, started, _clock(), result.Status,
      analysis, result.Planning, result.Execution, result.Validation, result.Publication,
      analysis?.OrderedFiles.Select(file => file.FullPath).ToArray() ?? [],
      analysis?.BookMetadata, plan?.Metadata, plan?.Cover?.ContentHash, plan?.Cover?.Origin.ToString(),
      result.Timings, result.Diagnostics, result.PublishedPath);
    try
    {
      await _logs.WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
      return result;
    }
    catch (Exception)
    {
      var diagnostics = result.Diagnostics.Concat([new ServiceDiagnostic("audit.write-failed", ServiceDiagnosticSeverity.Warning, "The job audit record could not be written.")]).ToArray();
      return result with { Diagnostics = Array.AsReadOnly(diagnostics) };
    }
  }

  private async Task CleanupAsync(string path, List<ServiceDiagnostic> diagnostics, List<ServiceStageTiming> timings)
  {
    try { await TimedAsync(ConversionStage.Cleanup, token => _cleaner.CleanupAsync(path, token), timings, CancellationToken.None).ConfigureAwait(false); }
    catch (Exception) { diagnostics.Add(new("cleanup.failed", ServiceDiagnosticSeverity.Warning, "The temporary conversion artifacts could not be removed.")); }
  }

  private async Task<T> TimedAsync<T>(ConversionStage stage, Func<CancellationToken, Task<T>> action, List<ServiceStageTiming> timings, CancellationToken cancellationToken)
  {
    var started = _clock();
    try { return await action(cancellationToken).ConfigureAwait(false); }
    finally { timings.Add(new(stage, started, _clock())); }
  }

  private async Task TimedAsync(ConversionStage stage, Func<CancellationToken, Task> action, List<ServiceStageTiming> timings, CancellationToken cancellationToken)
  {
    var started = _clock();
    try { await action(cancellationToken).ConfigureAwait(false); }
    finally { timings.Add(new(stage, started, _clock())); }
  }

  private static void AddAnalysisDiagnostics(IEnumerable<AnalysisDiagnostic> source, List<ServiceDiagnostic> destination)
    => destination.AddRange(source.Select(value => new ServiceDiagnostic(value.Code, value.Severity switch
    {
      AnalysisDiagnosticSeverity.Error => ServiceDiagnosticSeverity.Error,
      AnalysisDiagnosticSeverity.Warning => ServiceDiagnosticSeverity.Warning,
      _ => ServiceDiagnosticSeverity.Information,
    }, value.Message)));
}
