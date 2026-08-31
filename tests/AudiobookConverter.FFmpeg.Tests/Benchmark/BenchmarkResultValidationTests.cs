using AudiobookConverter.Benchmarks;
#pragma warning disable CA1707

namespace AudiobookConverter.FFmpeg.Tests.Benchmark;

public sealed class BenchmarkResultValidationTests
{
  [Fact]
  public void Validate_accepts_complete_anonymous_row()
  {
    var row = BenchmarkResult.Example.ToColumns();
    Assert.Empty(BenchmarkResultValidator.Validate(CsvBenchmarkWriter.Header.Split(','), row));
  }

  [Theory]
  [InlineData("case One")]
  [InlineData("case-c:\\private")]
  public void Validate_rejects_unsafe_case_ids(string caseId)
  {
    var row = BenchmarkResult.Example with { CaseId = caseId };
    Assert.Contains(BenchmarkResultValidator.Validate(CsvBenchmarkWriter.Header.Split(','), row.ToColumns()), error => error.Contains("case_id", StringComparison.Ordinal));
  }

  [Fact]
  public void Validate_rejects_absolute_private_paths_and_unknown_states()
  {
    var row = BenchmarkResult.Example with { ValidationReason = "C:\\private\\evidence.log", ValidationStatus = "maybe" };
    var errors = BenchmarkResultValidator.Validate(CsvBenchmarkWriter.Header.Split(','), row.ToColumns());
    Assert.Contains(errors, error => error.Contains("private path", StringComparison.Ordinal));
    Assert.Contains(errors, error => error.Contains("validation_status", StringComparison.Ordinal));
  }

  [Theory]
  [InlineData("NaN")]
  [InlineData("Infinity")]
  [InlineData("0")]
  public void Validate_rejects_bad_duration_or_ratio(string value)
  {
    var row = BenchmarkResult.Example.ToColumns();
    row[2] = value;
    row[14] = value;
    Assert.NotEmpty(BenchmarkResultValidator.Validate(CsvBenchmarkWriter.Header.Split(','), row));
  }

  [Fact]
  public void Validate_rejects_missing_required_columns()
  {
    var header = CsvBenchmarkWriter.Header.Split(',').Where(name => name != "strategy").ToArray();
    Assert.Contains(BenchmarkResultValidator.Validate(header, BenchmarkResult.Example.ToColumns()), error => error.Contains("missing required column", StringComparison.Ordinal));
  }

  [Fact]
  public void Validate_rejects_nonfinite_aggregate_realtime_factor()
  {
    var row = BenchmarkResult.Example.ToColumns();
    row[22] = "NaN";
    Assert.Contains(BenchmarkResultValidator.Validate(CsvBenchmarkWriter.Header.Split(','), row), error => error.Contains("aggregate_realtime_factor", StringComparison.Ordinal));
  }

  [Fact]
  public void ValidateCollection_reconstructs_consistent_concurrency_round()
  {
    var first = BenchmarkResult.Example with { Operation = "concurrency", RoundId = "round-c2-r1", Concurrency = 2, AggregateRealtimeFactor = 20m };
    var second = first with { RunId = BenchmarkResult.NewRunId() };
    Assert.Empty(BenchmarkResultValidator.ValidateCollection(CsvBenchmarkWriter.Header.Split(','), [first.ToColumns(), second.ToColumns()]));
  }

  [Fact]
  public void ValidateCollection_rejects_incomplete_or_inconsistent_concurrency_round()
  {
    var row = BenchmarkResult.Example with { Operation = "concurrency", RoundId = "round-c2-r1", Concurrency = 2, AggregateRealtimeFactor = 20m };
    Assert.Contains(BenchmarkResultValidator.ValidateCollection(CsvBenchmarkWriter.Header.Split(','), [row.ToColumns()]), error => error.Contains("member count", StringComparison.Ordinal));
    var inconsistent = row with { RunId = BenchmarkResult.NewRunId(), AggregateRealtimeFactor = 21m };
    Assert.Contains(BenchmarkResultValidator.ValidateCollection(CsvBenchmarkWriter.Header.Split(','), [row.ToColumns(), inconsistent.ToColumns()]), error => error.Contains("aggregate", StringComparison.Ordinal));
  }

  [Fact]
  public void ConcurrencySummary_recomputes_average_and_improvement()
  {
    var rows = new List<BenchmarkResult>();
    AddRound(rows, 1, 1, 74.059023631m); AddRound(rows, 1, 2, 72.6138202343m);
    AddRound(rows, 3, 1, 215.4684471995m); AddRound(rows, 3, 2, 206.4549563429m);
    AddRound(rows, 4, 1, 248.9511372721m); AddRound(rows, 4, 2, 241.1136688774m);
    AddRound(rows, 6, 1, 329.1433625971m); AddRound(rows, 8, 1, 338.3193700742m);
    Assert.Equal(73.33642193265m, BenchmarkConcurrencySummary.AverageAggregateRealtimeFactor(rows, 1));
    Assert.Equal(210.9617017712m, BenchmarkConcurrencySummary.AverageAggregateRealtimeFactor(rows, 3));
    Assert.Equal(16.15m, decimal.Round(BenchmarkConcurrencySummary.PercentImprovement(rows, 3, 4), 2));
    Assert.Equal(34.33m, decimal.Round(BenchmarkConcurrencySummary.PercentImprovement(rows, 4, 6), 2));
    Assert.Equal(2.79m, decimal.Round(BenchmarkConcurrencySummary.PercentImprovement(rows, 6, 8), 2));
  }

  private static void AddRound(List<BenchmarkResult> rows, int concurrency, int round, decimal aggregate)
  {
    for (var index = 0; index < concurrency; index++) rows.Add(BenchmarkResult.Example with { RunId = BenchmarkResult.NewRunId(), Operation = "concurrency", RoundId = $"round-c{concurrency}-r{round}", Concurrency = concurrency, AggregateRealtimeFactor = aggregate });
  }
}
