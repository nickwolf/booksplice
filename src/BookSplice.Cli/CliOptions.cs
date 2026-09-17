namespace BookSplice.Cli;

public sealed record CliOptions(
  string Source,
  string? OutputDirectory,
  string? QualityProfileId,
  int? BitrateKbps,
  int? Jobs,
  bool? CreateChapters,
  bool Overwrite,
  bool DryRun,
  bool Json);

public sealed record CliDiagnostic(string Code, string Message);

public sealed record CliParseResult(CliOptions? Options, IReadOnlyList<CliDiagnostic> Diagnostics)
{
  public bool IsSuccess => Options is not null && Diagnostics.Count == 0;
}
