using System.Text;

namespace BookSplice.Core.Naming;

public sealed record OutputTemplateValues(string? Title, string? Author = null, string? Series = null, string? SeriesPosition = null, string? Narrator = null, string? Year = null)
{
  internal string? Get(string token) => token switch
  {
    "title" => Title,
    "author" => Author,
    "series" => Series,
    "seriesposition" => SeriesPosition,
    "narrator" => Narrator,
    "year" => Year,
    _ => null,
  };
}

public sealed record ResolvedOutputSegment(string Text, bool Skipped, IReadOnlyList<string> MissingTokens);

/// <summary>A parsed output template such as {author}\{series}\{title}. The last segment is the file name.</summary>
public sealed class OutputPathTemplate
{
  public const string DefaultText = "{title}";
  public static IReadOnlyList<string> TokenNames { get; } = ["title", "author", "series", "seriesPosition", "narrator", "year"];

  private readonly List<List<Part>> _segments;
  private OutputPathTemplate(string text, List<List<Part>> segments) => (Text, _segments) = (text, segments);

  public string Text { get; }
  public static OutputPathTemplate Default { get; } = Parse(DefaultText);

  public static string? Validate(string? text) => TryParse(text, out _, out var error) ? null : error;

  public static OutputPathTemplate Parse(string? text)
    => TryParse(text, out var template, out var error) ? template! : throw new ArgumentException(error, nameof(text));

  public static bool TryParse(string? text, out OutputPathTemplate? template, out string? error)
  {
    template = null;
    if (string.IsNullOrWhiteSpace(text)) { error = "The output template is empty."; return false; }
    if (text.Length > 512) { error = "The output template is longer than 512 characters."; return false; }
    if (text.Any(char.IsControl)) { error = "The output template contains control characters."; return false; }
    if (text[0] is '\\' or '/' || Path.IsPathRooted(text)) { error = "The output template must be relative to the destination folder."; return false; }
    var segments = new List<List<Part>> { new() };
    var literal = new StringBuilder();
    void FlushLiteral() { if (literal.Length > 0) { segments[^1].Add(new(literal.ToString(), null)); literal.Clear(); } }
    for (var index = 0; index < text.Length; index++)
    {
      var character = text[index];
      if (character is '\\' or '/') { FlushLiteral(); segments.Add([]); }
      else if (character == '{')
      {
        var end = text.IndexOf('}', index);
        var name = end < 0 ? "" : text[(index + 1)..end];
        var canonical = TokenNames.FirstOrDefault(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase));
        if (canonical is null) { error = end < 0 || name.Contains('{') ? "The output template has an unclosed '{'." : $"Unknown output template field '{{{name}}}'. Use {string.Join(", ", TokenNames.Select(known => "{" + known + "}"))}."; return false; }
        FlushLiteral();
        segments[^1].Add(new("", canonical));
        index = end;
      }
      else if (character == '}') { error = "The output template has an unmatched '}'."; return false; }
      else literal.Append(character);
    }
    FlushLiteral();
    var last = segments[^1];
    var folders = segments.Take(segments.Count - 1).Where(segment => segment.Count > 0).ToList();
    foreach (var segment in folders.Append(last))
    {
      if (segment.Any(part => part.Token is not null)) continue;
      var literalText = string.Concat(segment.Select(part => part.Literal)).Trim();
      if (literalText.Length > 0 && literalText.All(c => c is '.' or ' ')) { error = "The output template may not contain '.' or '..' segments."; return false; }
    }
    folders.Add(last);
    template = new OutputPathTemplate(text, folders);
    error = null;
    return true;
  }

  public IReadOnlyList<ResolvedOutputSegment> Resolve(OutputTemplateValues values)
  {
    var resolved = new List<ResolvedOutputSegment>();
    for (var index = 0; index < _segments.Count; index++)
    {
      var segment = _segments[index];
      var builder = new StringBuilder();
      var missing = new List<string>();
      var tokenCount = 0;
      foreach (var part in segment)
      {
        if (part.Token is null) { builder.Append(part.Literal); continue; }
        tokenCount++;
        var value = values.Get(part.Token.ToLowerInvariant())?.Trim();
        if (string.IsNullOrEmpty(value)) missing.Add(part.Token);
        else builder.Append(value);
      }
      var text = builder.ToString();
      if (missing.Count > 0)
      {
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '-', '_', ',', ';', '.');
      }
      var allMissing = tokenCount > 0 && missing.Count == tokenCount;
      var isFile = index == _segments.Count - 1;
      resolved.Add(new(allMissing ? "" : text, allMissing && !isFile, missing));
    }
    return resolved;
  }

  public string DescribeSegment(int index) => string.Concat(_segments[index].Select(part => part.Token is null ? part.Literal : "{" + part.Token + "}"));
  public int SegmentCount => _segments.Count;

  private sealed record Part(string Literal, string? Token);
}
