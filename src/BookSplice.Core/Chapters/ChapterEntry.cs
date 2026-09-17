namespace BookSplice.Core.Chapters;

public sealed record ChapterEntry(
  long StartMicroseconds,
  long EndMicroseconds,
  string Title,
  string OriginalTitle,
  string SourceRelativePath);
