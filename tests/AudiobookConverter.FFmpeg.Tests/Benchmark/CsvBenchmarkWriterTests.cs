using AudiobookConverter.Benchmarks;
#pragma warning disable CA1707

namespace AudiobookConverter.FFmpeg.Tests.Benchmark;

public sealed class CsvBenchmarkWriterTests
{
  [Fact]
  public void Writer_uses_invariant_columns_and_anonymous_case_id()
  {
    var csv = CsvBenchmarkWriter.Write(new[] { BenchmarkResult.Example });
    Assert.StartsWith("run_id,case_id,source_seconds,input_codec", csv, StringComparison.Ordinal);
    Assert.Contains(",case-mp3-mono-096,", csv, StringComparison.Ordinal);
    Assert.DoesNotContain("P:\\", csv, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Writer_escapes_commas_quotes_and_newlines()
  {
    var result = BenchmarkResult.Example with { CaseId = "case,\"quoted\"\nvalue", ValidationStatus = "ok,\"yes\"" };
    var csv = CsvBenchmarkWriter.Write([result]);
    var expectedCase = "\"case,\"\"quoted\"\"\nvalue\"";
    Assert.Contains(expectedCase, csv, StringComparison.Ordinal);
    Assert.Contains("\"ok,\"\"yes\"\"\"", csv, StringComparison.Ordinal);
  }
}
