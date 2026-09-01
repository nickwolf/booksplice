using System.Globalization;

namespace AudiobookConverter.FFmpeg.Execution;

public sealed record ConversionProgress(long? OutTimeMicroseconds, double? Speed, string? State, int StageIndex);
public static class ConversionProgressParser
{
  public static ConversionProgress? Parse(string line, int stageIndex)
  {
    if (string.IsNullOrWhiteSpace(line)) return null;
    var split = line.IndexOf('=');
    if (split <= 0) return null;
    var key = line[..split];
    var value = line[(split + 1)..];
    return key switch
    {
      "out_time_us" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var time) => new(time, null, null, stageIndex),
      "speed" when double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) => new(null, speed, null, stageIndex),
      "progress" => new(null, null, value, stageIndex),
      _ => null
    };
  }
}
