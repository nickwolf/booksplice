namespace BookSplice.Core.Chapters;

public sealed record ChapterMark(long StartMicroseconds, string Title);

public enum ChapterMarkProblemCode
{
  NoChapters,
  InvalidBookLength,
  IndexOutOfRange,
  FirstNotAtZero,
  NotIncreasing,
  AfterEnd,
  TooShort,
  EmptyTitle,
  NoNextChapter,
  Overflow,
  Unavailable,
}

// Index is the offending mark, or -1 when the problem is about the whole list.
public sealed record ChapterMarkProblem(int Index, ChapterMarkProblemCode Code, string Message);

public sealed class ChapterMarkValidation
{
  public ChapterMarkValidation(IEnumerable<ChapterMarkProblem> problems) => Problems = Array.AsReadOnly(problems.ToArray());
  public IReadOnlyList<ChapterMarkProblem> Problems { get; }
  public bool IsValid => Problems.Count == 0;
}

public sealed class ChapterMarkEditResult
{
  private ChapterMarkEditResult(IReadOnlyList<ChapterMark>? marks, IReadOnlyList<ChapterMarkProblem> problems) => (Marks, Problems) = (marks, problems);
  public IReadOnlyList<ChapterMark>? Marks { get; }
  public IReadOnlyList<ChapterMarkProblem> Problems { get; }
  public bool IsAccepted => Marks is not null;

  public static ChapterMarkEditResult Accept(IReadOnlyList<ChapterMark> marks) => new(marks, []);
  public static ChapterMarkEditResult Reject(IEnumerable<ChapterMarkProblem> problems) => new(null, Array.AsReadOnly(problems.ToArray()));
}
