namespace BookSplice.Core.Chapters;

public static class ChapterTime
{
  // h:mm:ss.fff, with three more digits only when the value is not a whole millisecond, so every value round-trips.
  public static string Format(long microseconds)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(microseconds);
    var fraction = microseconds % 1_000_000;
    var totalSeconds = microseconds / 1_000_000;
    var text = $"{totalSeconds / 3600}:{totalSeconds / 60 % 60:D2}:{totalSeconds % 60:D2}.{fraction / 1000:D3}";
    return fraction % 1000 == 0 ? text : text + (fraction % 1000).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
  }

  // Accepts h:mm:ss, m:ss and ss, each with an optional fraction of up to six digits. Only the leading part may exceed 59.
  public static bool TryParse(string? text, out long microseconds)
  {
    microseconds = 0;
    if (text is null) return false;
    text = text.Trim();
    var dot = text.IndexOf('.');
    var whole = dot < 0 ? text : text[..dot];
    var fractionText = dot < 0 ? string.Empty : text[(dot + 1)..];
    if (dot >= 0 && (fractionText.Length is 0 or > 6 || !fractionText.All(char.IsAsciiDigit))) return false;
    var parts = whole.Split(':');
    if (parts.Length > 3) return false;
    long seconds = 0;
    for (var index = 0; index < parts.Length; index++)
    {
      var part = parts[index];
      if (part.Length == 0 || part.Length > 15 || !part.All(char.IsAsciiDigit)) return false;
      if (index > 0 && part.Length != 2) return false;
      var value = long.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
      if (index > 0 && value > 59) return false;
      try { seconds = checked(seconds * (index == 0 ? 1 : 60) + value); }
      catch (OverflowException) { return false; }
    }
    var fraction = fractionText.Length == 0 ? 0 : long.Parse(fractionText.PadRight(6, '0'), System.Globalization.CultureInfo.InvariantCulture);
    try { microseconds = checked(seconds * 1_000_000 + fraction); }
    catch (OverflowException) { return false; }
    return true;
  }
}
