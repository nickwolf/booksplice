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
}
