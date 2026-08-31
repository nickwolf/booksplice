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

  [Fact]
  public void Append_keeps_a_single_header_when_reopened()
  {
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
    try
    {
      CsvBenchmarkWriter.Append(path, BenchmarkResult.Example);
      CsvBenchmarkWriter.Append(path, BenchmarkResult.Example with { RunId = BenchmarkResult.NewRunId() });
      Assert.Equal(1, File.ReadLines(path).Count(line => line == CsvBenchmarkWriter.Header));
      Assert.Equal(3, File.ReadLines(path).Count());
    }
    finally { if (File.Exists(path)) File.Delete(path); }
  }

  [Fact]
  public void NewRunId_returns_nonempty_unique_ids()
  {
    var ids = Enumerable.Range(0, 32).Select(_ => BenchmarkResult.NewRunId()).ToArray();
    Assert.All(ids, id => Assert.Matches("^[a-f0-9]{32}$", id));
    Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
  }

  [Fact]
  public void Append_keeps_an_immediately_visible_row_when_later_work_throws()
  {
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
    try
    {
      CsvBenchmarkWriter.Append(path, BenchmarkResult.Example);
      Assert.Throws<InvalidOperationException>((Action)(() => throw new InvalidOperationException("later benchmark failed")));
      Assert.Contains("case-mp3-mono-096", File.ReadAllText(path), StringComparison.Ordinal);
    }
    finally { if (File.Exists(path)) File.Delete(path); }
  }

  [Fact]
  public async Task Append_keeps_an_immediately_visible_row_when_later_work_is_cancelled()
  {
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
    using var cancellation = new CancellationTokenSource();
    try
    {
      CsvBenchmarkWriter.Append(path, BenchmarkResult.Example);
      cancellation.Cancel();
      await Assert.ThrowsAsync<TaskCanceledException>(() => Task.FromCanceled(cancellation.Token));
      Assert.Contains("case-mp3-mono-096", File.ReadAllText(path), StringComparison.Ordinal);
    }
    finally { if (File.Exists(path)) File.Delete(path); }
  }
}
