namespace BookSplice.Gui.ViewModels;

// h:mm:ss for lengths and chapter starts. Whole seconds are truncated, hours are not wrapped at 24, and a negative value shows as zero.
internal static class DurationText
{
  public static string Format(TimeSpan duration)
  {
    var seconds = (long)Math.Max(0, duration.TotalSeconds);
    return FormattableString.Invariant($"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}");
  }
}
