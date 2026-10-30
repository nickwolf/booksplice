namespace BookSplice.Core.Discovery;

/// <summary>The audio files a folder contributes to a proposed book, in listing order.</summary>
public sealed record BookGroupPart(string Folder, IReadOnlyList<string> Files, bool IsLooseFile = false);

/// <summary>A proposed book: one or more folders' files, named after <see cref="RootFolder"/>.</summary>
public sealed class BookGroup
{
  public BookGroup(string rootFolder, IReadOnlyList<BookGroupPart> parts)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(rootFolder);
    ArgumentNullException.ThrowIfNull(parts);
    if (parts.Count == 0 || parts.Any(part => part.Files.Count == 0)) throw new ArgumentException("A book group needs at least one file in every part.", nameof(parts));
    RootFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootFolder));
    Parts = Array.AsReadOnly(parts.ToArray());
    Files = Array.AsReadOnly(parts.SelectMany(part => part.Files).ToArray());
  }

  /// <summary>The folder the book is named after and whose subtree holds every file.</summary>
  public string RootFolder { get; }
  public IReadOnlyList<BookGroupPart> Parts { get; }
  public IReadOnlyList<string> Files { get; }
  public bool IsLooseFile => Parts.Count == 1 && Parts[0].IsLooseFile;
  public bool CanSplit => Parts.Count > 1;
  public string Name => IsLooseFile ? Path.GetFileName(Files[0]) : SourceSelection.RootName(RootFolder);

  public SourceSelection ToSelection() => SourceSelection.Create(Files, RootFolder);

  /// <summary>One group per part, each named after its own folder.</summary>
  public IReadOnlyList<BookGroup> Split() => Parts.Select(part => new BookGroup(part.Folder, [part])).ToArray();

  /// <summary>Joins the groups in the given order into one rooted at their common folder.</summary>
  public static BookGroup Combine(IReadOnlyList<BookGroup> groups)
  {
    ArgumentNullException.ThrowIfNull(groups);
    if (groups.Count == 0) throw new ArgumentException("At least one group is required.", nameof(groups));
    if (groups.Count == 1) return groups[0];
    var root = SourceSelection.CommonAncestor(groups.Select(group => group.RootFolder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()) ?? groups[0].RootFolder;
    return new BookGroup(root, groups.SelectMany(group => group.Parts).ToArray());
  }
}
