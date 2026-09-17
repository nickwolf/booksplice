using System.Globalization;

namespace BookSplice.Benchmarks;

public sealed record BenchmarkResult(
  string RunId, string CaseId, decimal? SourceSeconds, string? InputCodec,
  int? InputChannels, int? InputSampleRate, decimal? WallSeconds,
  decimal? ChildCpuSeconds, string? OutputCodec, int? OutputTracks,
  int? OutputChannels, int? OutputSampleRate, decimal? OutputSeconds, long? OutputBytes,
  decimal? RealtimeFactor, string ValidationStatus, string ValidationReason = "",
  string Strategy = "direct-concat-transcode", int? TargetBitrateKbps = 128,
  string ChannelMode = "preserve", string ValidationMode = "lightweight", int? Concurrency = 1,
  decimal? AggregateRealtimeFactor = null, string StorageClass = "private-copy-to-local",
  string ManualResponsiveness = "not-measured", string Operation = "", string RoundId = "")
{
  public static BenchmarkResult Example => new("run-example", "case-mp3-mono-096", 2.0m, "mp3", 1, 44100, 0.2m, 0.1m, "aac", 1, 1, 44100, 2.0m, 4096, 10m, "ok");
  public static string NewRunId() => Guid.NewGuid().ToString("N");
  public string[] ToColumns() => [RunId, CaseId, Format(SourceSeconds), InputCodec ?? "", InputChannels?.ToString(CultureInfo.InvariantCulture) ?? "", InputSampleRate?.ToString(CultureInfo.InvariantCulture) ?? "", Format(WallSeconds), Format(ChildCpuSeconds), OutputCodec ?? "", OutputTracks?.ToString(CultureInfo.InvariantCulture) ?? "", OutputChannels?.ToString(CultureInfo.InvariantCulture) ?? "", OutputSampleRate?.ToString(CultureInfo.InvariantCulture) ?? "", Format(OutputSeconds), OutputBytes?.ToString(CultureInfo.InvariantCulture) ?? "", Format(RealtimeFactor), ValidationStatus, ValidationReason, Strategy, TargetBitrateKbps?.ToString(CultureInfo.InvariantCulture) ?? "", ChannelMode, ValidationMode, Concurrency?.ToString(CultureInfo.InvariantCulture) ?? "", Format(AggregateRealtimeFactor), StorageClass, ManualResponsiveness, Operation, RoundId];
  private static string Format(decimal? value) => value?.ToString("0.##########", CultureInfo.InvariantCulture) ?? "";
}
