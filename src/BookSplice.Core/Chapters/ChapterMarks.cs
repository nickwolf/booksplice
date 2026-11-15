using System.Globalization;
using System.Text.RegularExpressions;

namespace BookSplice.Core.Chapters;

public static partial class ChapterMarks
{
  [GeneratedRegex(@" \(\d+\)$", RegexOptions.CultureInvariant)]
  private static partial Regex NumberSuffix();

  public const long MinimumLengthMicroseconds = 1_000_000;

  public static ChapterMarkValidation Validate(IReadOnlyList<ChapterMark> marks, long totalMicroseconds)
  {
    ArgumentNullException.ThrowIfNull(marks);
    var problems = new List<ChapterMarkProblem>();
    void Add(int index, ChapterMarkProblemCode code, string message) => problems.Add(new ChapterMarkProblem(index, code, message));
    if (totalMicroseconds <= 0) Add(-1, ChapterMarkProblemCode.InvalidBookLength, "The book has no length to divide into chapters.");
    if (marks.Count == 0) Add(-1, ChapterMarkProblemCode.NoChapters, "There must be at least one chapter.");
    for (var index = 0; index < marks.Count; index++)
    {
      var mark = marks[index] ?? throw new ArgumentException("The chapter mark collection contains null.", nameof(marks));
      if (string.IsNullOrWhiteSpace(mark.Title)) Add(index, ChapterMarkProblemCode.EmptyTitle, "The title is empty.");
      if (index == 0 && mark.StartMicroseconds != 0) Add(index, ChapterMarkProblemCode.FirstNotAtZero, "The first chapter must start at 0:00:00.000");
      if (index > 0 && mark.StartMicroseconds <= marks[index - 1].StartMicroseconds) Add(index, ChapterMarkProblemCode.NotIncreasing, "Starts at or before the previous chapter");
      if (totalMicroseconds > 0 && mark.StartMicroseconds >= totalMicroseconds) { Add(index, ChapterMarkProblemCode.AfterEnd, "Starts at or after the end of the book"); continue; }
      var end = index + 1 < marks.Count ? marks[index + 1].StartMicroseconds : totalMicroseconds;
      if (end <= mark.StartMicroseconds) continue;
      try
      {
        if (checked(end - mark.StartMicroseconds) < MinimumLengthMicroseconds) Add(index, ChapterMarkProblemCode.TooShort, "Shorter than 1 second");
      }
      catch (OverflowException) { Add(index, ChapterMarkProblemCode.Overflow, "The chapter length is outside the supported range."); }
    }
    return new ChapterMarkValidation(problems);
  }

  public static IReadOnlyList<ChapterMark> FromChapterPlan(ChapterPlan plan)
  {
    ArgumentNullException.ThrowIfNull(plan);
    return plan.IsValid ? plan.Entries.Select(entry => new ChapterMark(entry.StartMicroseconds, entry.Title)).ToArray() : [];
  }

  public static ChapterMarkEditResult Split(IReadOnlyList<ChapterMark> marks, int index, long atMicroseconds, long totalMicroseconds)
  {
    if (!InRange(marks, index, out var rejected)) return rejected!;
    var result = marks.ToList();
    result.Insert(index + 1, new ChapterMark(atMicroseconds, SplitTitle(marks, marks[index].Title)));
    return Finish(result, totalMicroseconds);
  }

  public static ChapterMarkEditResult MergeWithNext(IReadOnlyList<ChapterMark> marks, int index, long totalMicroseconds)
  {
    if (!InRange(marks, index, out var rejected)) return rejected!;
    if (index == marks.Count - 1) return ChapterMarkEditResult.Reject([new(index, ChapterMarkProblemCode.NoNextChapter, "The last chapter has no next chapter to merge with.")]);
    var result = marks.ToList();
    result.RemoveAt(index + 1);
    return Finish(result, totalMicroseconds);
  }

  public static ChapterMarkEditResult MoveStart(IReadOnlyList<ChapterMark> marks, int index, long newStartMicroseconds, long totalMicroseconds)
  {
    if (!InRange(marks, index, out var rejected)) return rejected!;
    var result = marks.ToList();
    result[index] = marks[index] with { StartMicroseconds = newStartMicroseconds };
    return Finish(result, totalMicroseconds);
  }

  public static ChapterMarkEditResult Rename(IReadOnlyList<ChapterMark> marks, int index, string title, long totalMicroseconds)
  {
    if (!InRange(marks, index, out var rejected)) return rejected!;
    var result = marks.ToList();
    result[index] = marks[index] with { Title = title?.Trim() ?? string.Empty };
    return Finish(result, totalMicroseconds);
  }

  // Each mark becomes an entry that ends at the next start. Its source is the last track whose span starts at or before the mark.
  public static IReadOnlyList<ChapterEntry> ToEntries(IReadOnlyList<ChapterMark> marks, ChapterPlan trackChapters)
  {
    ArgumentNullException.ThrowIfNull(marks); ArgumentNullException.ThrowIfNull(trackChapters);
    if (!trackChapters.IsValid || trackChapters.Entries.Count == 0) throw new ArgumentException("The track chapters are unavailable.", nameof(trackChapters));
    var validation = Validate(marks, trackChapters.TotalDurationMicroseconds);
    if (!validation.IsValid) throw new ArgumentException(validation.Problems[0].Message, nameof(marks));
    var entries = new List<ChapterEntry>(marks.Count);
    for (var index = 0; index < marks.Count; index++)
    {
      var start = marks[index].StartMicroseconds;
      var source = trackChapters.Entries.LastOrDefault(entry => entry.StartMicroseconds <= start) ?? trackChapters.Entries[0];
      var end = index + 1 < marks.Count ? marks[index + 1].StartMicroseconds : trackChapters.TotalDurationMicroseconds;
      entries.Add(new ChapterEntry(start, end, marks[index].Title.Trim(), source.OriginalTitle, source.SourceRelativePath));
    }
    return entries;
  }

  // "<base> (n)" with the smallest n >= 2 no mark already uses, where base is the title without a trailing " (k)".
  private static string SplitTitle(IReadOnlyList<ChapterMark> marks, string title)
  {
    var baseTitle = NumberSuffix().Replace(title.Trim(), string.Empty);
    for (var number = 2; ; number++)
    {
      var candidate = string.Create(CultureInfo.InvariantCulture, $"{baseTitle} ({number})");
      if (!marks.Any(mark => string.Equals(mark.Title.Trim(), candidate, StringComparison.Ordinal))) return candidate;
    }
  }

  private static bool InRange(IReadOnlyList<ChapterMark> marks, int index, out ChapterMarkEditResult? rejected)
  {
    ArgumentNullException.ThrowIfNull(marks);
    rejected = index >= 0 && index < marks.Count ? null : ChapterMarkEditResult.Reject([new(index, ChapterMarkProblemCode.IndexOutOfRange, "There is no such chapter.")]);
    return rejected is null;
  }

  private static ChapterMarkEditResult Finish(List<ChapterMark> marks, long totalMicroseconds)
  {
    var validation = Validate(marks, totalMicroseconds);
    return validation.IsValid ? ChapterMarkEditResult.Accept(marks.ToArray()) : ChapterMarkEditResult.Reject(validation.Problems);
  }
}
