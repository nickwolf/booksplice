using BookSplice.Core.Discovery;

namespace BookSplice.Core.Planning;

public static class SourceDurationEvidence
{
  public static long? ReadMicroseconds(SourceFile file)
  {
    ArgumentNullException.ThrowIfNull(file);
    var duration = file.ProbeResult.Duration;
    if (duration is null or <= 0)
    {
      var streams = file.ProbeResult.AudioStreams.Where(stream => stream.Duration is > 0).ToArray();
      duration = streams.Length == 1 ? streams[0].Duration : null;
    }
    if (duration is null or <= 0) return null;

    try
    {
      var microseconds = checked((long)decimal.Round(checked(duration.Value * 1_000_000m), 0, MidpointRounding.ToEven));
      return microseconds > 0 ? microseconds : null;
    }
    catch (OverflowException) { return null; }
  }

  public static long? TotalMicroseconds(IReadOnlyList<long?> durations)
  {
    ArgumentNullException.ThrowIfNull(durations);
    if (durations.Count == 0) return null;
    long total = 0;
    try
    {
      foreach (var duration in durations)
      {
        if (duration is null or <= 0) return null;
        total = checked(total + duration.Value);
      }
    }
    catch (OverflowException) { return null; }
    return total;
  }
}
