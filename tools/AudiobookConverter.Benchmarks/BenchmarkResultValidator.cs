using System.Globalization;
using System.Text.RegularExpressions;

namespace AudiobookConverter.Benchmarks;

public static partial class BenchmarkResultValidator
{
  private static readonly string[] RequiredColumns = CsvBenchmarkWriter.Header.Split(',');
  private static readonly HashSet<string> ValidationStates = new(StringComparer.Ordinal) { "ok", "failed", "expected-failure", "not-run" };

  public static IReadOnlyList<string> Validate(IReadOnlyList<string> header, IReadOnlyList<string> row)
  {
    var errors = new List<string>();
    foreach (var required in RequiredColumns) if (!header.Contains(required, StringComparer.Ordinal)) errors.Add($"missing required column: {required}");
    if (errors.Count > 0 || row.Count != header.Count) return errors;
    var values = header.Zip(row).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
    if (!CaseId().IsMatch(values["case_id"])) errors.Add("case_id is unsafe");
    if (!ValidationStates.Contains(values["validation_status"])) errors.Add("validation_status is unknown");
    foreach (var value in values.Values) if (LooksPrivate(value)) { errors.Add("private path found in result row"); break; }
    foreach (var field in new[] { "source_seconds", "wall_seconds", "realtime_factor" }) if (!PositiveFinite(values[field])) errors.Add($"{field} must be finite and greater than zero");
    if (values["validation_status"] == "ok") foreach (var field in new[] { "strategy", "target_bitrate_kbps", "channel_mode", "validation_mode", "concurrency", "storage_class", "manual_responsiveness" }) if (string.IsNullOrWhiteSpace(values[field])) errors.Add($"completed row is missing {field}");
    return errors;
  }

  private static bool PositiveFinite(string value) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && number > 0;
  private static bool LooksPrivate(string value) => Path.IsPathFullyQualified(value) || value.Contains('\\') || value.Contains('/') || value.Contains('@');
  [GeneratedRegex("^case-[a-z0-9-]+$")]
  private static partial Regex CaseId();
}
