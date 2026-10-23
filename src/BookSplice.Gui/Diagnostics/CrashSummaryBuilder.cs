using System.Globalization;
using System.Text;
using BookSplice.Core.Analysis;

namespace BookSplice.Gui.Diagnostics;

public static class CrashSummaryBuilder
{
  public static string Build(IReadOnlyList<CrashReportEntry> entries, IReadOnlyList<string> exactPaths)
  {
    ArgumentNullException.ThrowIfNull(entries);
    var text = new StringBuilder("BookSplice crash summary").AppendLine();
    for (var index = 0; index < entries.Count; index++)
    {
      var report = entries[index].Report;
      text.AppendLine().Append("Report ").Append(index + 1).Append(" of ").Append(entries.Count).AppendLine();
      Line(text, "Version", report.AppVersion, exactPaths);
      Line(text, "Launch mode", report.LaunchMode, exactPaths);
      Line(text, "Time (UTC)", report.CreatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), exactPaths);
      Line(text, "Source", report.Source, exactPaths);
      Line(text, "Jobs", report.JobIds.Count == 0 ? "none" : string.Join(", ", report.JobIds), exactPaths);
      Line(text, "Exception", report.ExceptionType, exactPaths);
      Line(text, "Message", report.Message, exactPaths);
      Block(text, "Stack trace", report.StackTrace, exactPaths);
      foreach (var inner in report.InnerExceptions)
      {
        Line(text, $"Inner exception (depth {inner.Depth})", inner.Type, exactPaths);
        Line(text, "Message", inner.Message, exactPaths);
        Block(text, "Stack trace", inner.StackTrace, exactPaths);
      }
      if (report.Truncated) text.AppendLine("Some details were shortened to fit the report size limit.");
    }
    return text.ToString();
  }

  private static void Line(StringBuilder text, string label, string value, IReadOnlyList<string> exactPaths) =>
    text.Append(label).Append(": ").AppendLine(Clean(value, exactPaths));

  private static void Block(StringBuilder text, string label, string value, IReadOnlyList<string> exactPaths)
  {
    if (string.IsNullOrWhiteSpace(value)) return;
    text.Append(label).AppendLine(":");
    foreach (var line in value.Split('\n')) text.Append("  ").AppendLine(Clean(line, exactPaths).Trim());
  }

  // Sanitize flattens line breaks, so multi-line values are cleaned a line at a time.
  private static string Clean(string value, IReadOnlyList<string> exactPaths) =>
    string.Join(" ", value.Split('\n').Select(line =>
      PublicTextRedactor.Sanitize(PublicTextRedactor.RedactCredentials(line.TrimEnd('\r')), exactPaths).Trim()));
}
