using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Validation;

namespace BookSplice.Core.EditCopy;

// ChapterTitleEdits are keyed by 0-based chapter index. An empty tag value removes that field.
public sealed record EditCopyRequest(
  string SourcePath,
  IReadOnlyDictionary<SemanticField, string> TagEdits,
  IReadOnlyDictionary<int, string> ChapterTitleEdits,
  string DestinationPath,
  MetadataProfile Profile);

public enum EditCopyStatus { Published, InvalidInput, ValidationFailed, Cancelled, Failed }

public sealed class EditCopyResult
{
  public EditCopyResult(EditCopyStatus status, string? publishedPath, IEnumerable<ExecutionDiagnostic>? diagnostics = null, IEnumerable<ValidationCheck>? checks = null)
  {
    Status = status;
    PublishedPath = publishedPath is null ? null : Path.GetFullPath(publishedPath);
    Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
    Checks = Array.AsReadOnly((checks ?? []).ToArray());
  }

  public EditCopyStatus Status { get; }
  public string? PublishedPath { get; }
  public IReadOnlyList<ExecutionDiagnostic> Diagnostics { get; }
  public IReadOnlyList<ValidationCheck> Checks { get; }
}

public interface IEditCopyService
{
  Task<EditCopyResult> CreateAsync(EditCopyRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null);
}
