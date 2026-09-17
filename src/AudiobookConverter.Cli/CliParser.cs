using System.Globalization;
using AudiobookConverter.Core.Planning;

namespace AudiobookConverter.Cli;

public static class CliParser
{
  private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
  {
    "--output", "--quality", "--bitrate", "--jobs",
  };

  public static CliParseResult Parse(IReadOnlyList<string> arguments)
  {
    ArgumentNullException.ThrowIfNull(arguments);
    var diagnostics = new List<CliDiagnostic>();
    if (arguments.Any(ContainsControlCharacter))
      return Failure("usage.unsafe-character", "Arguments may not contain control characters.");

    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var switches = new HashSet<string>(StringComparer.Ordinal);
    var sources = new List<string>();
    for (var index = 0; index < arguments.Count; index++)
    {
      var argument = arguments[index];
      if (argument.Length == 0 || argument[0] != '-')
      {
        sources.Add(argument);
        continue;
      }

      if (ValueOptions.Contains(argument))
      {
        if (values.ContainsKey(argument)) diagnostics.Add(new("usage.duplicate-option", $"Option '{argument}' may be specified only once."));
        if (index + 1 >= arguments.Count || arguments[index + 1].Length > 0 && arguments[index + 1][0] == '-')
        {
          diagnostics.Add(new("usage.missing-value", $"Option '{argument}' requires a value."));
          continue;
        }

        var value = arguments[++index];
        values.TryAdd(argument, value);
        continue;
      }

      if (argument is "--chapters" or "--no-chapters" or "--overwrite" or "--dry-run" or "--json")
      {
        switches.Add(argument);
        continue;
      }

      diagnostics.Add(new("usage.unknown-option", $"Unknown option '{argument}'."));
    }

    if (sources.Count != 1) diagnostics.Add(new("usage.source-count", "Exactly one source path is required."));
    if (switches.Contains("--chapters") && switches.Contains("--no-chapters")) diagnostics.Add(new("usage.chapter-conflict", "Specify either --chapters or --no-chapters."));

    var bitrate = ParseRange(values, "--bitrate", 32, 320, "usage.bitrate-range", diagnostics);
    var jobs = ParseRange(values, "--jobs", 1, 32, "usage.jobs-range", diagnostics);
    values.TryGetValue("--quality", out var quality);
    if (quality is not null && QualityProfileCatalog.FindById(quality) is null)
      diagnostics.Add(new("usage.quality-profile", $"Unknown quality profile '{quality}'."));

    if (diagnostics.Count != 0) return new(null, diagnostics.AsReadOnly());
    values.TryGetValue("--output", out var output);
    bool? chapters = switches.Contains("--chapters") ? true : switches.Contains("--no-chapters") ? false : null;
    return new(new CliOptions(
      sources[0], output, quality, bitrate, jobs, chapters,
      switches.Contains("--overwrite"), switches.Contains("--dry-run"), switches.Contains("--json")), []);
  }

  private static int? ParseRange(Dictionary<string, string> values, string option, int minimum, int maximum, string code, List<CliDiagnostic> diagnostics)
  {
    if (!values.TryGetValue(option, out var text)) return null;
    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
    {
      diagnostics.Add(new(code, $"Option '{option}' must be between {minimum} and {maximum}."));
      return null;
    }
    return value;
  }

  private static bool ContainsControlCharacter(string value) => value.Any(char.IsControl);
  private static CliParseResult Failure(string code, string message) => new(null, [new(code, message)]);
}
