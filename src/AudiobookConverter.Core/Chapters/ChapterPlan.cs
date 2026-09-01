namespace AudiobookConverter.Core.Chapters;

public sealed class ChapterPlan
{
  private ChapterPlan(bool isValid, IReadOnlyList<ChapterEntry> entries, long totalDurationMicroseconds, string? errorCode, string? errorMessage)
  {
    IsValid = isValid;
    Entries = entries;
    TotalDurationMicroseconds = totalDurationMicroseconds;
    ErrorCode = errorCode;
    ErrorMessage = errorMessage;
  }

  public bool IsValid { get; }
  public IReadOnlyList<ChapterEntry> Entries { get; }
  public long TotalDurationMicroseconds { get; }
  public string? ErrorCode { get; }
  public string? ErrorMessage { get; }

  public static ChapterPlan Valid(IEnumerable<ChapterEntry> entries, long totalDurationMicroseconds)
    => new(true, entries.ToArray(), totalDurationMicroseconds, null, null);

  public static ChapterPlan Invalid(string code, string message)
    => new(false, Array.Empty<ChapterEntry>(), 0, code, message);
}
