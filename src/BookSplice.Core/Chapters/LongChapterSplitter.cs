using System.Globalization;

namespace BookSplice.Core.Chapters;

/// <summary>Splits chapters too long for the MP4 chapter text track, whose sample durations are 32-bit counts of audio samples.</summary>
public static class LongChapterSplitter
{
  // One second of samples below int.MaxValue, so rounding in the muxer cannot push a chapter over.
  public static long MaximumMicroseconds(int sampleRate)
  {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
    return (int.MaxValue - (long)sampleRate) * 1_000_000L / sampleRate;
  }

  public static IReadOnlyList<ChapterEntry> Split(IReadOnlyList<ChapterEntry> chapters, int sampleRate)
  {
    ArgumentNullException.ThrowIfNull(chapters);
    var maximum = MaximumMicroseconds(sampleRate);
    if (chapters.All(chapter => chapter.EndMicroseconds - chapter.StartMicroseconds <= maximum)) return chapters;
    var result = new List<ChapterEntry>();
    foreach (var chapter in chapters)
    {
      var duration = chapter.EndMicroseconds - chapter.StartMicroseconds;
      if (duration <= maximum) { result.Add(chapter); continue; }
      var parts = (duration + maximum - 1) / maximum;
      for (var part = 1L; part <= parts; part++)
      {
        var start = chapter.StartMicroseconds + duration * (part - 1) / parts;
        var end = part == parts ? chapter.EndMicroseconds : chapter.StartMicroseconds + duration * part / parts;
        result.Add(chapter with { StartMicroseconds = start, EndMicroseconds = end, Title = string.Create(CultureInfo.InvariantCulture, $"{chapter.Title} (part {part} of {parts})") });
      }
    }
    return result;
  }
}
