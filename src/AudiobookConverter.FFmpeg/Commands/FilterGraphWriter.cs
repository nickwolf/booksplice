using AudiobookConverter.Core.Planning;

namespace AudiobookConverter.FFmpeg.Commands;

public sealed class FilterGraphWriter
{
  public static string Write(int sourceCount, QualityProfile profile, int sampleRate, int channels)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(sourceCount, 2);
    if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
    var layout = channels == 1 ? "mono" : "stereo";
    var pads = Enumerable.Range(0, sourceCount).Select(index => $"[{index}:a]aresample={sampleRate},aformat=sample_rates={sampleRate}:channel_layouts={layout}[a{index}]");
    return string.Join(";", pads) + ";" + string.Concat(Enumerable.Range(0, sourceCount).Select(index => $"[a{index}]")) + $"concat=n={sourceCount}:v=0:a=1[aout]\n";
  }
}
