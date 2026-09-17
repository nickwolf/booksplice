using AudiobookConverter.Core.Planning;

namespace AudiobookConverter.Core.Execution;

public enum ExecutionStatus { Succeeded, Failed, Cancelled }

public sealed record ExecutionDiagnostic(string Code, string Message);

public sealed record ExecutionProcessResult(
  int ExitCode,
  string StandardOutput,
  string StandardError,
  TimeSpan? ChildCpuTime = null,
  string? Executable = null,
  IReadOnlyList<string>? Arguments = null);

public sealed record ConversionExecutionResult(
  ExecutionStatus Status,
  string? TemporaryOutputPath,
  IReadOnlyList<ExecutionProcessResult> Processes,
  IReadOnlyList<ExecutionDiagnostic> Diagnostics);

public sealed record ConversionProgress(
  long? OutTimeMicroseconds,
  double? Speed,
  string? State,
  int StageIndex,
  int? SourceIndex = null,
  double? DisplayPercent = null);

public interface IConversionExecutor
{
  Task<ConversionExecutionResult> ExecuteAsync(
    ConversionPlan plan,
    CancellationToken cancellationToken,
    IProgress<ConversionProgress>? progress = null);
}
