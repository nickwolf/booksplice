using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.History;

public enum HistoryOutcome { Completed, Cancelled, Failed, Preview }

public sealed record ConversionHistoryEntry(
  Guid JobId,
  string FileName,
  DateTimeOffset StartedAt,
  TimeSpan Elapsed,
  HistoryOutcome Outcome,
  bool HasWarnings,
  string Summary,
  string Strategy,
  string ValidationLevel,
  IReadOnlyList<string> Warnings,
  string? OutputPath,
  bool OutputExists)
{
  public string OutcomeText => Outcome switch
  {
    HistoryOutcome.Completed => HasWarnings ? "Completed with warnings" : "Completed",
    HistoryOutcome.Cancelled => "Cancelled",
    HistoryOutcome.Preview => "Preview only",
    _ => "Failed",
  };
  public string WhenText => StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);
  public string ElapsedText => ConversionProgressDisplay.FormatDuration(Elapsed);
  public string WarningCountText => Warnings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
  public bool CanOpenOutput => OutputPath is not null && OutputExists;
  public string OutputText => OutputPath is null ? "No output was published." : OutputExists ? OutputPath : $"{OutputPath} (file is missing)";
  public string OutputName => OutputPath is null ? "" : System.IO.Path.GetFileName(OutputPath);
  public string Details => string.Join(Environment.NewLine, new[]
  {
    OutcomeText, Summary, $"Output: {OutputText}", $"Elapsed: {ElapsedText}", $"Strategy: {Strategy}", $"Validation level: {ValidationLevel}",
    Warnings.Count == 0 ? "" : "Warnings:" + Environment.NewLine + string.Join(Environment.NewLine, Warnings.Select(value => "  " + value)),
  }.Where(value => value.Length > 0));

  public bool Matches(string query)
    => string.IsNullOrWhiteSpace(query) || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term =>
      Details.Contains(term, StringComparison.CurrentCultureIgnoreCase) || JobId.ToString("D").Contains(term, StringComparison.OrdinalIgnoreCase));
}

public sealed record SkippedHistoryRecord(string FileName, string Reason);

public sealed record ConversionHistoryLoad(IReadOnlyList<ConversionHistoryEntry> Entries, IReadOnlyList<SkippedHistoryRecord> Skipped, int NotLoaded);
