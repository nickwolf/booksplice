using BookSplice.Core.Analysis;
using BookSplice.Core.Planning;

namespace BookSplice.Core.Validation;

public sealed record ValidationCheck(string Code, bool Passed, string Message, bool Required = true);

public sealed class ValidationReport
{
  public ValidationReport(string temporaryOutputPath, bool isValid, IEnumerable<ValidationCheck> checks, MediaProbeResult? outputFacts = null, IEnumerable<string>? warnings = null, IEnumerable<string>? errors = null, Guid? planId = null)
  {
    TemporaryOutputPath = Path.GetFullPath(temporaryOutputPath);
    IsValid = isValid;
    Checks = Array.AsReadOnly(checks.ToArray());
    OutputFacts = outputFacts;
    Warnings = Array.AsReadOnly((warnings ?? []).ToArray());
    Errors = Array.AsReadOnly((errors ?? []).ToArray());
    PlanId = planId;
  }

  public string TemporaryOutputPath { get; }
  public bool IsValid { get; }
  public IReadOnlyList<ValidationCheck> Checks { get; }
  public MediaProbeResult? OutputFacts { get; }
  public IReadOnlyList<string> Warnings { get; }
  public IReadOnlyList<string> Errors { get; }
  public Guid? PlanId { get; }

  public static ValidationReport Passed(string temporaryOutputPath, IEnumerable<ValidationCheck> checks, Guid? planId = null) => new(temporaryOutputPath, true, checks, planId: planId);
  public static ValidationReport Failed(string temporaryOutputPath, IEnumerable<ValidationCheck> checks, IEnumerable<string>? errors = null, Guid? planId = null) => new(temporaryOutputPath, false, checks, errors: errors, planId: planId);
}

public interface IOutputValidator
{
  Task<ValidationReport> ValidateAsync(ConversionPlan plan, string temporaryOutputPath, CancellationToken cancellationToken);
}

public enum PublicationStatus { Published, Rejected, Failed }

public sealed class PublicationResult
{
  public PublicationResult(PublicationStatus status, string? finalPath, IEnumerable<string>? diagnostics = null)
  {
    Status = status;
    FinalPath = finalPath is null ? null : Path.GetFullPath(finalPath);
    Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
  }

  public PublicationStatus Status { get; }
  public string? FinalPath { get; }
  public IReadOnlyList<string> Diagnostics { get; }
}

public interface IAtomicPublisher
{
  Task<PublicationResult> PublishAsync(ConversionPlan plan, ValidationReport report, CancellationToken cancellationToken);
}
