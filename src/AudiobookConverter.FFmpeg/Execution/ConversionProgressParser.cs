using System.Globalization;
using AudiobookConverter.Core.Execution;

namespace AudiobookConverter.FFmpeg.Execution;

public static class ConversionProgressParser
{
  public static ConversionProgress? Parse(string line, int stageIndex, int? sourceIndex = null)
  {
    if (string.IsNullOrWhiteSpace(line)) return null;
    var split = line.IndexOf('=');
    if (split <= 0) return null;
    var key = line[..split];
    var value = line[(split + 1)..].Trim();
    return key switch
    {
      "out_time_us" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var time) => new(time, null, null, stageIndex, sourceIndex),
      "speed" when double.TryParse(value.TrimEnd('x', 'X'), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) => new(null, speed, null, stageIndex, sourceIndex),
      "speed" => new(null, null, null, stageIndex, sourceIndex),
      "progress" => new(null, null, value, stageIndex, sourceIndex),
      _ => null
    };
  }
}
