using System.Text.RegularExpressions;

namespace BookSplice.Core.Chapters;

public static partial class ChapterTitleCleaner
{
  [GeneratedRegex(@"^\s*(?:(?:track|chapter)\s*)?\d{1,3}\s*[-._:)]+\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex TransportPrefix();

  public static string Clean(string? title, int chapterNumber)
  {
    var original = title?.Trim() ?? string.Empty;
    var cleaned = TransportPrefix().Replace(original, string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(cleaned) || cleaned.All(char.IsDigit)) return $"Chapter {chapterNumber}";
    return cleaned;
  }
}
