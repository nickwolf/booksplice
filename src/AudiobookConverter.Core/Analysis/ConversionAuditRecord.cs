using AudiobookConverter.Core.Execution;
using AudiobookConverter.Core.Metadata;
using AudiobookConverter.Core.Planning;
using AudiobookConverter.Core.Validation;

namespace AudiobookConverter.Core.Analysis;

public sealed record ConversionAuditRecord(
  int SchemaVersion,
  Guid JobId,
  DateTimeOffset StartedAt,
  DateTimeOffset FinishedAt,
  ConversionTerminalStatus TerminalStatus,
  BookAnalysis? Analysis,
  ConversionPlanningResult? Planning,
  ConversionExecutionResult? Execution,
  ValidationReport? Validation,
  PublicationResult? Publication,
  IReadOnlyList<string> OrderedSources,
  BookMetadata? ImportedMetadata,
  BookMetadata? FinalMetadata,
  string? SelectedCoverHash,
  string? SelectedCoverOrigin,
  IReadOnlyList<ServiceStageTiming> Timings,
  IReadOnlyList<ServiceDiagnostic> Diagnostics,
  string? PublishedPath)
{
  public const int CurrentSchemaVersion = 1;
}
