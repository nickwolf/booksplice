using System.Text;
using BookSplice.Core.Analysis;

namespace BookSplice.Core.Naming;

public sealed class OutputNamePlanner
{
  public const int MaxComponentLength = 240;
  private readonly StringComparer _comparison = StringComparer.OrdinalIgnoreCase;
  private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
  {
    "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
    "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
  };

  public string CreateRequestedPath(
    string destinationDirectory,
    string? editedOrReliableTitle,
    string? sourceRootName,
    CollisionPolicy collisionPolicy = CollisionPolicy.AvoidCollision,
    IReadOnlySet<string>? occupiedNames = null)
    => Plan(destinationDirectory, OutputPathTemplate.Default, new OutputTemplateValues(editedOrReliableTitle), sourceRootName, collisionPolicy, occupiedNames).Path;

  public OutputPathPlan Plan(
    string destinationDirectory,
    OutputPathTemplate template,
    OutputTemplateValues values,
    string? sourceRootName,
    CollisionPolicy collisionPolicy = CollisionPolicy.AvoidCollision,
    IReadOnlySet<string>? occupiedNames = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
    ArgumentNullException.ThrowIfNull(template);
    ArgumentNullException.ThrowIfNull(values);
    var destination = Path.GetFullPath(destinationDirectory);
    var notes = new List<AnalysisDiagnostic>();
    var resolved = template.Resolve(values);
    var folder = destination;
    for (var index = 0; index < resolved.Count - 1; index++)
    {
      var segment = resolved[index];
      var cleaned = segment.Skipped ? "" : Clean(segment.Text);
      if (cleaned.Length == 0)
      {
        var missing = segment.MissingTokens.Count > 0 ? string.Join(", ", segment.MissingTokens.Distinct()) : "a usable value";
        notes.Add(new("output.folder-skipped", AnalysisDiagnosticSeverity.Information, $"The folder \"{template.DescribeSegment(index)}\" was skipped because {missing} is not available."));
        continue;
      }
      folder = Path.Combine(folder, Fit(cleaned, 0, string.Empty));
    }

    var file = resolved[^1];
    var fileText = Clean(file.Text);
    foreach (var token in file.MissingTokens.Distinct())
      if (fileText.Length > 0) notes.Add(new("output.field-missing", AnalysisDiagnosticSeverity.Information, $"{token} is not available, so it is left out of the file name."));
    if (fileText.Length == 0)
    {
      fileText = FirstMeaningful(values.Title, sourceRootName, "Audiobook");
      if (template.Text != OutputPathTemplate.DefaultText) notes.Add(new("output.filename-fallback", AnalysisDiagnosticSeverity.Information, $"The file name template resolved to nothing, so \"{Clean(fileText)}\" is used."));
    }

    if (collisionPolicy == CollisionPolicy.AvoidCollision || collisionPolicy == CollisionPolicy.Overwrite)
      occupiedNames ??= Observe(folder);
    var candidate = Sanitize(fileText) + ".m4b";
    var requested = candidate;
    if (collisionPolicy == CollisionPolicy.AvoidCollision)
    {
      var ordinal = 1;
      while (IsOccupied(candidate, occupiedNames))
      {
        ordinal++;
        candidate = Sanitize(fileText, ordinal) + ".m4b";
      }
      if (ordinal > 1) notes.Add(new("output.collision-renamed", AnalysisDiagnosticSeverity.Information, $"{requested} already exists in the output folder, so this book will be saved as {candidate}."));
    }
    else if (IsOccupied(candidate, occupiedNames))
      notes.Add(new("output.collision-overwrite", AnalysisDiagnosticSeverity.Information, $"{candidate} already exists in the output folder and will be replaced."));

    var path = Path.Combine(folder, candidate);
    var full = Path.GetFullPath(path);
    var root = Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar;
    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The output path left the destination folder.");
    return new OutputPathPlan(path, notes);
  }

  private bool IsOccupied(string candidate, IReadOnlySet<string>? occupied)
    => occupied?.Any(name => _comparison.Equals(Path.GetFileName(name), candidate)) == true;

  private static HashSet<string> Observe(string destination)
    => Directory.Exists(destination)
      ? new HashSet<string>(Directory.EnumerateFileSystemEntries(destination).Select(Path.GetFileName).Where(name => name is not null)!, StringComparer.OrdinalIgnoreCase)
      : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

  private static string FirstMeaningful(params string?[] values)
    => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))!.Trim();

  private static string Clean(string value)
  {
    var builder = new StringBuilder(value.Length);
    foreach (var character in value)
    {
      if (char.IsControl(character) || "<>:\"/\\|?*".Contains(character)) builder.Append('_');
      else builder.Append(character);
    }

    return builder.ToString().Trim().TrimEnd(' ', '.');
  }

  private static string Sanitize(string value, int ordinal = 1)
  {
    var cleaned = Clean(value);
    if (cleaned.Length == 0) cleaned = "Audiobook";
    return Fit(cleaned, ".m4b".Length, ordinal == 1 ? string.Empty : $" ({ordinal})");
  }

  private static string Fit(string cleaned, int extension, string suffix)
  {
    var maxBaseLength = MaxComponentLength - extension - suffix.Length;
    if (cleaned.Length > maxBaseLength) cleaned = cleaned[..maxBaseLength].TrimEnd(' ', '.');
    if (ReservedNames.Contains(cleaned.Split('.')[0])) cleaned = $"_{cleaned}";
    if (cleaned.Length > maxBaseLength) cleaned = cleaned[..maxBaseLength].TrimEnd(' ', '.');
    return cleaned + suffix;
  }
}

public sealed record OutputPathPlan(string Path, IReadOnlyList<AnalysisDiagnostic> Notes);
