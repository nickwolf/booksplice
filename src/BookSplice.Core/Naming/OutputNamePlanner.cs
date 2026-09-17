using System.Text;

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
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
    var destination = Path.GetFullPath(destinationDirectory);
    var baseName = Sanitize(FirstMeaningful(editedOrReliableTitle, sourceRootName, "Audiobook"));
    var candidate = baseName + ".m4b";
    if (collisionPolicy == CollisionPolicy.AvoidCollision)
    {
      occupiedNames ??= Observe(destination);
      var ordinal = 1;
      while (IsOccupied(candidate, occupiedNames))
      {
        ordinal++;
        candidate = Sanitize(FirstMeaningful(editedOrReliableTitle, sourceRootName, "Audiobook"), ordinal) + ".m4b";
      }
    }

    return Path.Combine(destination, candidate);
  }

  private bool IsOccupied(string candidate, IReadOnlySet<string>? occupied)
    => occupied?.Any(name => _comparison.Equals(Path.GetFileName(name), candidate)) == true;

  private static HashSet<string> Observe(string destination)
    => Directory.Exists(destination)
      ? new HashSet<string>(Directory.EnumerateFileSystemEntries(destination).Select(Path.GetFileName).Where(name => name is not null)!, StringComparer.OrdinalIgnoreCase)
      : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

  private static string FirstMeaningful(params string?[] values)
    => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))!.Trim();

  private static string Sanitize(string value, int ordinal = 1)
  {
    var builder = new StringBuilder(value.Length);
    foreach (var character in value)
    {
      if (char.IsControl(character) || "<>:\"/\\|?*".Contains(character)) builder.Append('_');
      else builder.Append(character);
    }

    var cleaned = builder.ToString().Trim().TrimEnd(' ', '.');
    if (cleaned.Length == 0) cleaned = "Audiobook";
    var extension = ".m4b".Length;
    var suffix = ordinal == 1 ? string.Empty : $" ({ordinal})";
    var maxBaseLength = MaxComponentLength - extension - suffix.Length;
    if (cleaned.Length > maxBaseLength) cleaned = cleaned[..maxBaseLength].TrimEnd(' ', '.');
    if (ReservedNames.Contains(cleaned.Split('.')[0])) cleaned = $"_{cleaned}";
    if (cleaned.Length > maxBaseLength) cleaned = cleaned[..maxBaseLength].TrimEnd(' ', '.');
    return cleaned + suffix;
  }
}
