using System.Text.RegularExpressions;

namespace BookSplice.Core.Analysis;

public static partial class PublicTextRedactor
{
  public static string Sanitize(string value, IReadOnlyList<string>? exactPaths = null)
  {
    ArgumentNullException.ThrowIfNull(value);
    var clean = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
    clean = AbsoluteFilePath().Replace(clean, "[path]");
    if (exactPaths is not null)
    {
      foreach (var path in exactPaths.Where(path => !string.IsNullOrWhiteSpace(path)).OrderByDescending(path => path.Length))
        clean = clean.Replace(path, "[source]", StringComparison.OrdinalIgnoreCase);
    }
    return AbsolutePath().Replace(clean, "[path]");
  }

  public static string RedactCredentials(string value)
  {
    ArgumentNullException.ThrowIfNull(value);
    var clean = SecretAssignment().Replace(value, "$1$2[redacted]");
    clean = BearerToken().Replace(clean, "$1[redacted]");
    clean = ProviderToken().Replace(clean, "[redacted]");
    return LongBlob().Replace(clean, "[redacted]");
  }

  [GeneratedRegex(@"\b(password|passwd|pwd|token|secret|api[_-]?key|access[_-]?key|authorization)(\s*[:=]\s*)(?:(?:Bearer|Basic)\s+)?(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
  private static partial Regex SecretAssignment();

  [GeneratedRegex(@"\b(Bearer\s+)[A-Za-z0-9._~+/=-]{8,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
  private static partial Regex BearerToken();

  [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})", RegexOptions.CultureInvariant)]
  private static partial Regex ProviderToken();

  [GeneratedRegex(@"(?<![A-Za-z0-9+/=_-])(?:[0-9a-fA-F]{32,}|(?=[A-Za-z0-9+/]*[0-9])(?=[A-Za-z0-9+/]*[a-z])(?=[A-Za-z0-9+/]*[A-Z])[A-Za-z0-9+/]{40,}={0,2})(?![A-Za-z0-9+/=_-])", RegexOptions.CultureInvariant)]
  private static partial Regex LongBlob();

  [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\|/)[^\r\n]*?\.[A-Za-z0-9]{1,8}(?=[\s,;:.\)\x22\x27]|$)", RegexOptions.CultureInvariant)]
  private static partial Regex AbsoluteFilePath();

  [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\|/)[^\r\n]*", RegexOptions.CultureInvariant)]
  private static partial Regex AbsolutePath();
}
