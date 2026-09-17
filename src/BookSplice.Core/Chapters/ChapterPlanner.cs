using BookSplice.Core.Discovery;

namespace BookSplice.Core.Chapters;

public interface IChapterPlanner
{
  ChapterPlan Create(IReadOnlyList<SourceFile> orderedFiles, bool enabled);
}

public sealed class ChapterPlanner : IChapterPlanner
{
  public ChapterPlan Create(IReadOnlyList<SourceFile> orderedFiles, bool enabled)
  {
    ArgumentNullException.ThrowIfNull(orderedFiles);
    if (!enabled) return ChapterPlan.Valid(Array.Empty<ChapterEntry>(), 0);

    var entries = new List<ChapterEntry>(orderedFiles.Count);
    long cursor = 0;
    for (var index = 0; index < orderedFiles.Count; index++)
    {
      var file = orderedFiles[index] ?? throw new ArgumentException("The source file collection contains null.", nameof(orderedFiles));
      if (!TryDurationMicroseconds(file, out var duration, out var error))
        return ChapterPlan.Invalid(error!.Value.Code, error.Value.Message);

      long end;
      try { end = checked(cursor + duration); }
      catch (OverflowException) { return ChapterPlan.Invalid("duration.overflow", "The cumulative chapter duration exceeds the supported range."); }

      var original = SelectTitle(file);
      entries.Add(new ChapterEntry(cursor, end, ChapterTitleCleaner.Clean(original, index + 1), original, file.RelativePath));
      cursor = end;
    }

    return ChapterPlan.Valid(entries, cursor);
  }

  private static bool TryDurationMicroseconds(SourceFile file, out long value, out (string Code, string Message)? error)
  {
    var duration = file.ProbeResult.Duration;
    if (duration is null or <= 0)
    {
      var streams = file.ProbeResult.AudioStreams.Where(stream => stream.Duration is > 0).ToArray();
      duration = streams.Length == 1 ? streams[0].Duration : null;
    }

    if (duration is null or <= 0)
    {
      value = 0;
      error = ("duration.missing", $"No positive duration is available for '{file.RelativePath}'.");
      return false;
    }

    try
    {
      value = checked((long)decimal.Round(duration.Value * 1_000_000m, 0, MidpointRounding.ToEven));
      error = null;
      return true;
    }
    catch (OverflowException)
    {
      value = 0;
      error = ("duration.unrepresentable", $"The duration for '{file.RelativePath}' cannot be represented in microseconds.");
      return false;
    }
  }

  private static string SelectTitle(SourceFile file)
  {
    var tagged = file.ProbeResult.RawTags.FirstOrDefault(pair => string.Equals(pair.Key, "TITLE", StringComparison.OrdinalIgnoreCase)).Value;
    if (string.IsNullOrWhiteSpace(tagged))
      tagged = file.ProbeResult.FormatTags.FirstOrDefault(pair => string.Equals(pair.Key, "TITLE", StringComparison.OrdinalIgnoreCase)).Value;
    if (!string.IsNullOrWhiteSpace(tagged)) return tagged;
    var path = file.RelativePath.Replace('\\', '/');
    var name = path[(path.LastIndexOf('/') + 1)..];
    var extension = name.LastIndexOf('.');
    return extension > 0 ? name[..extension] : name;
  }
}
