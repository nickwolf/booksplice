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

  [Fact]
  public void Append_writes_one_header_and_flushes_each_row()
  {
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
    try
    {
      CsvBenchmarkWriter.Append(path, BenchmarkResult.Example);
      Assert.Contains("case-mp3-mono-096", File.ReadAllText(path), StringComparison.Ordinal);
      CsvBenchmarkWriter.Append(path, BenchmarkResult.Example with { RunId = Guid.NewGuid().ToString("N") });
      Assert.Equal(1, File.ReadAllLines(path).Count(x => x.StartsWith("run_id,", StringComparison.Ordinal)));
      Assert.Equal(3, File.ReadAllLines(path).Length);
    }
    finally { if (File.Exists(path)) File.Delete(path); }
  }

  [Fact]
  public void Append_formats_decimals_invariantly()
  {
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
    var prior = System.Globalization.CultureInfo.CurrentCulture;
    try { System.Globalization.CultureInfo.CurrentCulture = new("fr-FR"); CsvBenchmarkWriter.Append(path, BenchmarkResult.Example with { SourceSeconds = 1.25m }); Assert.Contains(",1.25,", File.ReadAllText(path), StringComparison.Ordinal); }
    finally { System.Globalization.CultureInfo.CurrentCulture = prior; if (File.Exists(path)) File.Delete(path); }
  }
}
