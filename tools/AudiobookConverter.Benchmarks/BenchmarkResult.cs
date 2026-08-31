using System.Globalization;

namespace AudiobookConverter.Benchmarks;

public sealed record BenchmarkResult(
  string RunId, string CaseId, decimal? SourceSeconds, string? InputCodec,
  int? InputChannels, int? InputSampleRate, decimal? WallSeconds,
  decimal? ChildCpuSeconds, string? OutputCodec, int? OutputTracks,
  int? OutputChannels, int? OutputSampleRate, decimal? OutputSeconds, long? OutputBytes,
  decimal? RealtimeFactor, string ValidationStatus, string ValidationReason = "")
{
  public static BenchmarkResult Example => new("run-example", "case-mp3-mono-096", 2.0m, "mp3", 1, 44100, 0.2m, 0.1m, "aac", 1, 1, 44100, 2.0m, 4096, 10m, "ok");
  public static string NewRunId() => Guid.NewGuid().ToString("N");
  public string[] ToColumns() => [RunId, CaseId, Format(SourceSeconds), InputCodec ?? "", InputChannels?.ToString(CultureInfo.InvariantCulture) ?? "", InputSampleRate?.ToString(CultureInfo.InvariantCulture) ?? "", Format(WallSeconds), Format(ChildCpuSeconds), OutputCodec ?? "", OutputTracks?.ToString(CultureInfo.InvariantCulture) ?? "", OutputChannels?.ToString(CultureInfo.InvariantCulture) ?? "", OutputSampleRate?.ToString(CultureInfo.InvariantCulture) ?? "", Format(OutputSeconds), OutputBytes?.ToString(CultureInfo.InvariantCulture) ?? "", Format(RealtimeFactor), ValidationStatus, ValidationReason];
  private static string Format(decimal? value) => value?.ToString("0.##########", CultureInfo.InvariantCulture) ?? "";
}
