using System.Text.RegularExpressions;

namespace AudiobookConverter.Core.Analysis;

public static partial class PublicTextRedactor
{
  public static string Sanitize(string value, IReadOnlyList<string>? exactPaths = null)
  {
    ArgumentNullException.ThrowIfNull(value);
    var clean = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
    if (exactPaths is not null)
    {
      foreach (var path in exactPaths.Where(path => !string.IsNullOrWhiteSpace(path)).OrderByDescending(path => path.Length))
        clean = clean.Replace(path, "[source]", StringComparison.OrdinalIgnoreCase);
    }
    return AbsolutePath().Replace(clean, "[path]");
  }

  [GeneratedRegex(@"(?<![A-Za-z0-9])(?:(?:[A-Za-z]:[\\/]|\\\\|/)[^\r\n]*?\.[A-Za-z0-9]{1,8}(?=[\s,;:\)]|$)|(?:[A-Za-z]:[\\/]|\\\\|/)[^\r\n]*)", RegexOptions.CultureInvariant)]
  private static partial Regex AbsolutePath();
}
