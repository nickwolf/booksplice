using System.Text;

namespace AudiobookConverter.Benchmarks;

public static class CsvBenchmarkWriter
{
  public const string Header = "run_id,case_id,source_seconds,input_codec,input_channels,input_sample_rate,wall_seconds,child_cpu_seconds,output_codec,output_tracks,output_channels,output_sample_rate,output_seconds,output_bytes,realtime_factor,validation_status,validation_reason,strategy,target_bitrate_kbps,channel_mode,validation_mode,concurrency,aggregate_realtime_factor,storage_class,manual_responsiveness";
  public static string Write(IEnumerable<BenchmarkResult> results)
  {
    var builder = new StringBuilder().AppendLine(Header);
    foreach (var result in results) builder.AppendLine(string.Join(',', result.ToColumns().Select(Escape)));
    return builder.ToString();
  }
  public static void Append(string path, BenchmarkResult result)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    var needsHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
    using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false);
    if (needsHeader) writer.WriteLine(Header);
    writer.WriteLine(string.Join(',', result.ToColumns().Select(Escape)));
    writer.Flush();
    stream.Flush(flushToDisk: true);
  }
  private static string Escape(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? '"' + value.Replace("\"", "\"\"") + '"' : value;
}
