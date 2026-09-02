namespace AudiobookConverter.FFmpeg.Commands;

public sealed class FilterGraphWriter
{
  public static string Write(int sourceCount, int sampleRate, int channels)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(sourceCount, 2);
    if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
    var layout = channels == 1 ? "mono" : "stereo";
    var pads = Enumerable.Range(0, sourceCount).Select(index => $"[{index}:a]aresample={sampleRate},aformat=sample_fmts=fltp:sample_rates={sampleRate}:channel_layouts={layout}[a{index}]");
    return string.Join(";", pads) + ";" + string.Concat(Enumerable.Range(0, sourceCount).Select(index => $"[a{index}]")) + $"concat=n={sourceCount}:v=0:a=1[aout]\n";
  }
  public static void WriteFile(string path, int sourceCount, int sampleRate, int channels) => File.WriteAllText(path, Write(sourceCount, sampleRate, channels), new System.Text.UTF8Encoding(false));
}
