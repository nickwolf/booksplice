namespace BookSplice.FFmpeg.Execution;

// One slice of the output timeline, [Start, End) in samples. Its worker encodes the wider [ExtendedStart, ExtendedEnd) so the encoder is settled at both cut points.
internal sealed record SegmentRange(int Index, long Start, long End, long ExtendedStart, long ExtendedEnd, IReadOnlyList<int> Inputs, long TrimStart, bool IsFirst, bool IsLast)
{
  public int ExpectedFrames => checked((int)((ExtendedEnd - ExtendedStart + SegmentRangePlanner.FrameSamples - 1) / SegmentRangePlanner.FrameSamples)) + 1;
  public int FirstKeptFrame => IsFirst ? 0 : checked((int)((Start - ExtendedStart) / SegmentRangePlanner.FrameSamples)) + 1;
  public int LastKeptFrame => IsLast ? ExpectedFrames - 1 : checked((int)((End - ExtendedStart) / SegmentRangePlanner.FrameSamples));
  public long TrimEnd => TrimStart + (ExtendedEnd - ExtendedStart);
}

internal static class SegmentRangePlanner
{
  public const int FrameSamples = 1024;
  public const int PaddingSamples = 43 * FrameSamples;
  public const int MaximumInputs = 32;
  public const int MinimumFrames = 64;

  // Cut points sit on the encoder's 1024-sample frame grid, so a cut is a frame boundary. Ranges can start mid-file.
  public static IReadOnlyList<SegmentRange> Plan(IReadOnlyList<long> sampleCounts, int jobs)
  {
    ArgumentNullException.ThrowIfNull(sampleCounts);
    var starts = new long[sampleCounts.Count + 1];
    for (var index = 0; index < sampleCounts.Count; index++) starts[index + 1] = checked(starts[index] + sampleCounts[index]);
    var total = starts[^1];
    if (total <= 0) throw new ArgumentException("There are no samples to encode.", nameof(sampleCounts));
    var wanted = (int)Math.Clamp(Math.Max(1, jobs) * 2L, 1, Math.Max(1, total / (MinimumFrames * FrameSamples)));
    var cuts = new List<long> { 0 };
    for (var step = 1; step < wanted; step++) cuts.Add(total * step / wanted / FrameSamples * FrameSamples);
    cuts.Add(total);
    while (true)
    {
      var oversized = -1;
      for (var index = 0; index + 1 < cuts.Count && oversized < 0; index++)
        if (cuts[index + 1] - cuts[index] >= 2 * MinimumFrames * FrameSamples && Build(starts, cuts[index], cuts[index + 1], index, false, false).Inputs.Count > MaximumInputs) oversized = index;
      if (oversized < 0) break;
      cuts.Insert(oversized + 1, (cuts[oversized] + cuts[oversized + 1]) / 2 / FrameSamples * FrameSamples);
    }
    return Enumerable.Range(0, cuts.Count - 1).Select(index => Build(starts, cuts[index], cuts[index + 1], index, index == 0, index == cuts.Count - 2)).ToArray();
  }

  private static SegmentRange Build(long[] starts, long start, long end, int index, bool first, bool last)
  {
    var total = starts[^1];
    var from = Math.Max(0, start - PaddingSamples);
    var to = Math.Min(total, end + PaddingSamples);
    var inputs = Enumerable.Range(0, starts.Length - 1).Where(file => starts[file] < to && starts[file + 1] > from && starts[file + 1] > starts[file]).ToArray();
    return new SegmentRange(index, start, end, from, to, inputs, from - starts[inputs[0]], first, last);
  }
}
