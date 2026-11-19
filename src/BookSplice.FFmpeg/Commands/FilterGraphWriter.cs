namespace BookSplice.FFmpeg.Commands;

public sealed class FilterGraphWriter
{
  public static string Write(int sourceCount, int sampleRate, int channels)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(sourceCount, 2);
    if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
    var pads = Enumerable.Range(0, sourceCount).Select(index => $"[{index}:a]{SourceChain(sampleRate, channels)}[a{index}]");
    return string.Join(";", pads) + ";" + string.Concat(Enumerable.Range(0, sourceCount).Select(index => $"[a{index}]")) + $"concat=n={sourceCount}:v=0:a=1[aout]\n";
  }

  public static string SourceChain(int sampleRate, int channels)
  {
    if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
    return $"aresample={sampleRate},aformat=sample_fmts=fltp:sample_rates={sampleRate}:channel_layouts={(channels == 1 ? "mono" : "stereo")}";
  }

  // The inputs joined and cut to [trimStart, trimEnd) samples, renumbered from zero so the encoder sees a gapless stream.
  public static string WriteRange(int inputCount, int sampleRate, int channels, long trimStart, long trimEnd)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(inputCount, 1);
    var pads = Enumerable.Range(0, inputCount).Select(index => $"[{index}:a]{SourceChain(sampleRate, channels)}[a{index}]");
    var joined = inputCount == 1 ? "[a0]" : string.Concat(Enumerable.Range(0, inputCount).Select(index => $"[a{index}]")) + $"concat=n={inputCount}:v=0:a=1[cat];[cat]";
    return string.Join(";", pads) + ";" + joined + $"atrim=start_sample={trimStart}:end_sample={trimEnd},asetpts=N/SR/TB[aout]";
  }

  public static void WriteFile(string path, int sourceCount, int sampleRate, int channels) => File.WriteAllText(path, Write(sourceCount, sampleRate, channels), new System.Text.UTF8Encoding(false));
}
