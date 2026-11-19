using System.Globalization;
using System.Text.RegularExpressions;

namespace BookSplice.FFmpeg.Execution;

// The verbose summary counts the samples the encoder was given. out_time_us can be one sample short of that count.
internal static partial class SourceSampleCounter
{
  public static long Parse(string standardError, out string summaryLine)
  {
    var match = Summary().Match(standardError);
    if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var samples)) throw new SegmentedAssemblyException("A source length could not be measured.");
    summaryLine = match.Value;
    return samples;
  }

  [GeneratedRegex(@"^.*Output stream #0:0 \(audio\): [0-9]+ frames encoded \(([0-9]+) samples\).*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
  private static partial Regex Summary();
}
