using System.Globalization;

namespace AudiobookConverter.Benchmarks;

public sealed record BenchmarkResult(
  string RunId, string CaseId, decimal? SourceSeconds, string? InputCodec,
  int? InputChannels, int? InputSampleRate, decimal? WallSeconds,
  decimal? ChildCpuSeconds, string? OutputCodec, int? OutputTracks,
  long? OutputBytes, decimal? RealtimeFactor, string ValidationStatus, string ValidationReason = "")
{
  public static BenchmarkResult Example => new("run-example", "case-mp3-mono-096", 2.0m, "mp3", 1, 44100, 0.2m, 0.1m, "aac", 1, 4096, 0.1m, "ok");
  public string[] ToColumns() => [RunId, CaseId, Format(SourceSeconds), InputCodec ?? "", InputChannels?.ToString(CultureInfo.InvariantCulture) ?? "", InputSampleRate?.ToString(CultureInfo.InvariantCulture) ?? "", Format(WallSeconds), Format(ChildCpuSeconds), OutputCodec ?? "", OutputTracks?.ToString(CultureInfo.InvariantCulture) ?? "", OutputBytes?.ToString(CultureInfo.InvariantCulture) ?? "", Format(RealtimeFactor), ValidationStatus, ValidationReason];
  private static string Format(decimal? value) => value?.ToString("0.##########", CultureInfo.InvariantCulture) ?? "";
}
