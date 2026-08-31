namespace AudiobookConverter.Benchmarks;

public sealed record BenchmarkRunOptions(
  string Strategy = "direct-concat-transcode",
  int TargetBitrateKbps = 128,
  string ChannelMode = "preserve",
  string ValidationMode = "lightweight",
  int Concurrency = 1,
  string StorageClass = "private-copy-to-local")
{
  public static readonly int[] NativeAacLcBitrates = [48, 56, 64, 72, 80, 96, 128, 160];
  public bool StreamCopy => Strategy == "compatible-stream-copy";
}
