namespace BookSplice.Core.Discovery;

public sealed record DiscoveryDiagnostic(string Code, string Message, string? FullPath = null, string? RelativePath = null);

public sealed class DiscoveryResult
{
  public DiscoveryResult(IEnumerable<SourceFile> files, IEnumerable<DiscoveryDiagnostic> errors, IEnumerable<DiscoveryDiagnostic> warnings)
  {
    ArgumentNullException.ThrowIfNull(files);
    ArgumentNullException.ThrowIfNull(errors);
    ArgumentNullException.ThrowIfNull(warnings);
    Files = Array.AsReadOnly(files.ToArray());
    Errors = Array.AsReadOnly(errors.ToArray());
    Warnings = Array.AsReadOnly(warnings.ToArray());
  }

  public IReadOnlyList<SourceFile> Files { get; }
  public IReadOnlyList<DiscoveryDiagnostic> Errors { get; }
  public IReadOnlyList<DiscoveryDiagnostic> Warnings { get; }
}
