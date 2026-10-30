using System.Text.RegularExpressions;

namespace BookSplice.Core.Discovery;

public interface IBookFolderEnumerator
{
  IEnumerable<string> EnumerateEntries(string directory);
  FileAttributes GetAttributes(string path);
}

public sealed class FileSystemBookFolderEnumerator : IBookFolderEnumerator
{
  public IEnumerable<string> EnumerateEntries(string directory) => Directory.EnumerateFileSystemEntries(directory);
  public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
}

public sealed record BookGroupScanProgress(int Folders, int Files);

/// <summary>Proposed groups plus every path that was skipped (reparse point) or could not be inspected.</summary>
public sealed class BookGroupPlan(IEnumerable<BookGroup> groups, IEnumerable<DiscoveryDiagnostic> notInspected)
{
  public IReadOnlyList<BookGroup> Groups { get; } = Array.AsReadOnly(groups.ToArray());
  public IReadOnlyList<DiscoveryDiagnostic> NotInspected { get; } = Array.AsReadOnly(notInspected.ToArray());
}

/// <summary>Proposes one book per folder that holds audio. Disc subfolders merge into the folder above them. Files are never probed.</summary>
public sealed class BookGroupPlanner(IBookFolderEnumerator? enumerator = null)
{
  private static readonly Regex DiscFolder = new(@"^(?:(?:disc|disk|cd|part)[\s_.-]*\d{1,3}|\d{1,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
  private readonly IBookFolderEnumerator _enumerator = enumerator ?? new FileSystemBookFolderEnumerator();

  public static bool IsDiscFolderName(string name) => DiscFolder.IsMatch(name.Trim());

  public Task<BookGroupPlan> PlanAsync(string parentPath, IProgress<BookGroupScanProgress>? progress = null, CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
    return Task.Run(() => Plan(parentPath, progress, cancellationToken), cancellationToken);
  }

  public BookGroupPlan Plan(string parentPath, IProgress<BookGroupScanProgress>? progress = null, CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
    cancellationToken.ThrowIfCancellationRequested();
    var scan = new Scan(_enumerator, progress, cancellationToken);
    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
    if (SourceDiscoverer.FindReparsePoint(root) is { } reparsePoint)
      scan.Problems.Add(new DiscoveryDiagnostic("discovery.reparse-point-skipped", "A reparse point was skipped during source discovery.", reparsePoint, Path.GetFileName(reparsePoint)));
    else if (!Directory.Exists(root))
      scan.Problems.Add(new DiscoveryDiagnostic("discovery.input-unreadable", "The input path does not exist or cannot be read.", root, Path.GetFileName(root)));
    else if (scan.List(root) is { } listing)
    {
      foreach (var file in listing.Files) scan.Groups.Add(new BookGroup(root, [new BookGroupPart(root, [file], IsLooseFile: true)]));
      foreach (var directory in listing.Directories) scan.PlanBook(directory);
    }
    return new BookGroupPlan(scan.Groups, scan.Problems.OrderBy(problem => problem.FullPath, StringComparer.OrdinalIgnoreCase));
  }

  private sealed record Listing(List<string> Files, List<string> Directories);

  private sealed class Scan(IBookFolderEnumerator enumerator, IProgress<BookGroupScanProgress>? progress, CancellationToken cancellationToken)
  {
    private int _folders;
    private int _files;
    public List<BookGroup> Groups { get; } = [];
    public List<DiscoveryDiagnostic> Problems { get; } = [];

    public Listing? List(string directory)
    {
      cancellationToken.ThrowIfCancellationRequested();
      string[] entries;
      try { entries = enumerator.EnumerateEntries(directory).ToArray(); }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
        Problems.Add(new DiscoveryDiagnostic("discovery.path-unreadable", exception.Message, directory, Path.GetFileName(directory)));
        return null;
      }

      var listing = new Listing([], []);
      foreach (var entry in entries.Order(StringComparer.OrdinalIgnoreCase))
      {
        cancellationToken.ThrowIfCancellationRequested();
        FileAttributes attributes;
        try { attributes = enumerator.GetAttributes(entry); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
          Problems.Add(new DiscoveryDiagnostic("discovery.path-unreadable", exception.Message, entry, Path.GetFileName(entry)));
          continue;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
          Problems.Add(new DiscoveryDiagnostic("discovery.reparse-point-skipped", "A reparse point was skipped during source discovery.", entry, Path.GetFileName(entry)));
        else if ((attributes & FileAttributes.Directory) != 0) listing.Directories.Add(Path.GetFullPath(entry));
        else if (SupportedAudio.IsSupported(entry)) listing.Files.Add(Path.GetFullPath(entry));
      }

      _folders++;
      _files += listing.Files.Count;
      progress?.Report(new BookGroupScanProgress(_folders, _files));
      return listing;
    }

    // Folders without audio and without disc subfolders propose nothing, but their other subfolders are still planned.
    public void PlanBook(string directory)
    {
      if (List(directory) is not { } listing) return;
      var parts = new List<BookGroupPart>();
      if (listing.Files.Count > 0) parts.Add(new BookGroupPart(directory, listing.Files));
      foreach (var disc in listing.Directories.Where(path => IsDiscFolderName(Path.GetFileName(path))))
      {
        var files = new List<string>();
        CollectFiles(disc, files);
        if (files.Count > 0) parts.Add(new BookGroupPart(disc, files));
      }
      if (parts.Count > 0) Groups.Add(new BookGroup(directory, parts));
      foreach (var other in listing.Directories.Where(path => !IsDiscFolderName(Path.GetFileName(path)))) PlanBook(other);
    }

    private void CollectFiles(string directory, List<string> files)
    {
      if (List(directory) is not { } listing) return;
      files.AddRange(listing.Files);
      foreach (var child in listing.Directories) CollectFiles(child, files);
    }
  }
}
