using System.Globalization;
using System.Text.RegularExpressions;

namespace BookSplice.Benchmarks;

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
    if (!string.IsNullOrWhiteSpace(values["aggregate_realtime_factor"]) && !PositiveFinite(values["aggregate_realtime_factor"])) errors.Add("aggregate_realtime_factor must be finite and greater than zero");
    if (values["operation"] is not ("" or "concurrency")) errors.Add("operation is unknown");
    if (values["operation"] == "concurrency" && (!RoundId().IsMatch(values["round_id"]) || !PositiveFinite(values["aggregate_realtime_factor"]))) errors.Add("concurrency row requires safe round_id and aggregate_realtime_factor");
    if (values["operation"] != "concurrency" && (!string.IsNullOrWhiteSpace(values["round_id"]) || !string.IsNullOrWhiteSpace(values["aggregate_realtime_factor"]))) errors.Add("ordinary row cannot contain concurrency aggregate evidence");
    if (values["validation_status"] == "ok") foreach (var field in new[] { "strategy", "target_bitrate_kbps", "channel_mode", "validation_mode", "concurrency", "storage_class", "manual_responsiveness" }) if (string.IsNullOrWhiteSpace(values[field])) errors.Add($"completed row is missing {field}");
    return errors;
  }

  public static IReadOnlyList<string> ValidateCollection(IReadOnlyList<string> header, IReadOnlyList<string[]> rows)
  {
    var errors = rows.SelectMany(row => Validate(header, row)).ToList();
    if (errors.Count > 0) return errors;
    var runIds = rows.Select(row => header.Zip(row).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal)["run_id"]);
    if (runIds.Count() != runIds.Distinct(StringComparer.Ordinal).Count()) errors.Add("duplicate run_id values");
    var concurrencyRows = rows.Select(row => header.Zip(row).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal)).Where(row => row["operation"] == "concurrency");
    foreach (var round in concurrencyRows.GroupBy(row => row["round_id"], StringComparer.Ordinal))
    {
      var expected = int.Parse(round.First()["concurrency"], CultureInfo.InvariantCulture);
      if (round.Count() != expected) errors.Add($"round {round.Key} member count does not match concurrency");
      if (round.Any(row => row["concurrency"] != expected.ToString(CultureInfo.InvariantCulture))) errors.Add($"round {round.Key} has inconsistent concurrency");
      if (round.Select(row => row["aggregate_realtime_factor"]).Distinct(StringComparer.Ordinal).Count() != 1) errors.Add($"round {round.Key} has inconsistent aggregate_realtime_factor");
      var measured = round.Sum(row => decimal.Parse(row["source_seconds"], CultureInfo.InvariantCulture)) / round.Max(row => decimal.Parse(row["wall_seconds"], CultureInfo.InvariantCulture));
      var recorded = decimal.Parse(round.First()["aggregate_realtime_factor"], CultureInfo.InvariantCulture);
      if (Math.Abs(measured - recorded) > 0.000001m) errors.Add($"round {round.Key} aggregate_realtime_factor does not match member timing");
    }
    return errors;
  }

  private static bool PositiveFinite(string value) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && number > 0;
  private static bool LooksPrivate(string value) => Path.IsPathFullyQualified(value) || value.Contains('\\') || value.Contains('/') || value.Contains('@');
  [GeneratedRegex("^case-[a-z0-9-]+$")]
  private static partial Regex CaseId();
  [GeneratedRegex("^round-c[1-9][0-9]*-r[1-9][0-9]*$")]
  private static partial Regex RoundId();
}
