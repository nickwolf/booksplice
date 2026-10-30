namespace BookSplice.Core.Discovery;

/// <summary>An explicit set of audio files that make up one book. Nothing outside the set is ever discovered.</summary>
public sealed class SourceSelection : IEquatable<SourceSelection>
{
  private SourceSelection(IReadOnlyList<string> paths, string rootPath, bool sharesRoot, IReadOnlyList<string> directories)
    => (Paths, RootPath, SharesRoot, Directories) = (paths, rootPath, sharesRoot, directories);

  /// <summary>Full paths in selection order, without case-insensitive repeats.</summary>
  public IReadOnlyList<string> Paths { get; }
  /// <summary>The deepest directory containing every file, or the first file's directory when the files span volumes.</summary>
  public string RootPath { get; }
  public bool SharesRoot { get; }
  /// <summary>Distinct directories that directly contain a selected file.</summary>
  public IReadOnlyList<string> Directories { get; }
  public string Name => RootName(RootPath);

  public static SourceSelection Create(IEnumerable<string> paths) => CreateCore(paths, null);

  /// <summary>Names the book after <paramref name="rootPath"/>, which must contain every file, instead of the deepest common directory.</summary>
  public static SourceSelection Create(IEnumerable<string> paths, string rootPath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
    return CreateCore(paths, rootPath);
  }

  private static SourceSelection CreateCore(IEnumerable<string> paths, string? rootPath)
  {
    ArgumentNullException.ThrowIfNull(paths);
    var full = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var raw in paths)
    {
      ArgumentException.ThrowIfNullOrWhiteSpace(raw);
      var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
      if (seen.Add(path)) full.Add(path);
    }
    if (full.Count == 0) throw new ArgumentException("At least one file is required.", nameof(paths));
    var directories = full.Select(path => Path.GetDirectoryName(path) ?? path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var common = CommonAncestor(directories);
    if (rootPath is not null)
    {
      var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
      if (common is null || !IsSameOrUnder(root, common)) throw new ArgumentException("Every file must be inside the root folder.", nameof(rootPath));
      common = root;
    }
    return new SourceSelection(Array.AsReadOnly(full.ToArray()), common ?? directories[0], common is not null, Array.AsReadOnly(directories));
  }

  /// <summary>The path relative to the common ancestor, or the full path when the files span volumes.</summary>
  public string FileId(string fullPath)
    => SharesRoot ? Path.GetRelativePath(RootPath, fullPath).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/') : fullPath;

  public static string RootName(string path)
  {
    var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    return string.IsNullOrWhiteSpace(name) || name.EndsWith(':') ? "Audiobook" : name;
  }

  public bool Equals(SourceSelection? other)
    => other is not null && Paths.Count == other.Paths.Count && Paths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(other.Paths);

  public override bool Equals(object? obj) => Equals(obj as SourceSelection);

  public override int GetHashCode()
  {
    var hash = 0;
    foreach (var path in Paths) hash ^= StringComparer.OrdinalIgnoreCase.GetHashCode(path);
    return hash;
  }

  private static bool IsSameOrUnder(string root, string directory)
  {
    var relative = Path.GetRelativePath(root, directory);
    return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
  }

  internal static string? CommonAncestor(IReadOnlyList<string> directories)
  {
    var root = Path.GetPathRoot(directories[0]);
    if (string.IsNullOrEmpty(root) || directories.Any(directory => !string.Equals(Path.GetPathRoot(directory), root, StringComparison.OrdinalIgnoreCase))) return null;
    string[] Segments(string directory) => directory.Length <= root.Length ? [] : directory[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    var common = Segments(directories[0]);
    foreach (var directory in directories.Skip(1))
    {
      var segments = Segments(directory);
      var length = 0;
      while (length < common.Length && length < segments.Length && string.Equals(common[length], segments[length], StringComparison.OrdinalIgnoreCase)) length++;
      common = common[..length];
    }
    return common.Length == 0 ? root : Path.Combine(root, Path.Combine(common));
  }
}
