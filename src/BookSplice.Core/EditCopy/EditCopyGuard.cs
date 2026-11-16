using BookSplice.Core.Discovery;
using BookSplice.Core.Execution;

namespace BookSplice.Core.EditCopy;

public interface IEditCopyFileSystem
{
  bool FileExists(string path);
  bool DirectoryExists(string path);
  string? GetPhysicalId(string path);
}

public sealed class FileSystemEditCopyFileSystem : IEditCopyFileSystem
{
  public bool FileExists(string path) => File.Exists(path);
  public bool DirectoryExists(string path) => Directory.Exists(path);

  public string? GetPhysicalId(string path) => SourceIdentity.TryGetPhysicalId(path);
}

public static class EditCopyCodes
{
  public const string SourceMissing = "edit.source-missing";
  public const string SourceNotM4b = "edit.source-not-m4b";
  public const string SourceCheckFailed = "edit.source-check-failed";
  public const string NoEdits = "edit.no-edits";
  public const string DestinationInvalid = "edit.destination-invalid";
  public const string DestinationIsSource = "edit.destination-is-source";
  public const string TagNotInProfile = "edit.tag-not-in-profile";
  public const string TagConflict = "edit.tag-conflict";
  public const string TagValueInvalid = "edit.tag-value-invalid";
  public const string TagUnsupported = "edit.tag-unsupported";
  public const string ChaptersUnsupportedLayout = "edit.chapters-unsupported-layout";
  public const string ChaptersChanged = "edit.chapters-changed";
  public const string ChapterIndexInvalid = "edit.chapter-index-invalid";
  public const string ChapterTitleInvalid = "edit.chapter-title-invalid";
  public const string SourceChanged = "edit.source-changed";
  public const string Cancelled = "edit.cancelled";
  public const string Failed = "edit.failed";
  public const string ProcessFailed = "edit.process-failed";
  public const string ValidationFailed = "edit.validation-failed";
  public const string PublicationFailed = "edit.publication-failed";

  public const string CheckAudioPackets = "edit.audio-packets";
  public const string CheckCover = "edit.cover";
  public const string CheckChapterCount = "edit.chapters-count";
  public const string CheckChapterTiming = "edit.chapters-timing";
  public const string CheckChapterTitles = "edit.chapters-titles";
  public const string CheckEditedTags = "edit.tags-edited";
  public const string CheckUntouchedTags = "edit.tags-untouched";
  public const string CheckMetadataBoxes = "edit.metadata-boxes";
  public const string CheckStreams = "edit.streams";
  public const string CheckDuration = "edit.duration";
}

// Checks that need no media parsing. Nothing is written anywhere when this returns a diagnostic.
public static class EditCopyGuard
{
  public static IReadOnlyList<ExecutionDiagnostic> Check(EditCopyRequest request, IEditCopyFileSystem fileSystem)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(fileSystem);
    var diagnostics = new List<ExecutionDiagnostic>();
    if (!TryFullPath(request.SourcePath, out var source) || !fileSystem.FileExists(source))
      return [new(EditCopyCodes.SourceMissing, "The file to edit does not exist.")];
    if (!string.Equals(Path.GetExtension(source), ".m4b", StringComparison.OrdinalIgnoreCase))
      return [new(EditCopyCodes.SourceNotM4b, "Only .m4b files can be edited.")];
    if ((request.TagEdits?.Count ?? 0) == 0 && (request.ChapterTitleEdits?.Count ?? 0) == 0)
      return [new(EditCopyCodes.NoEdits, "There are no changes to save.")];
    foreach (var (index, title) in request.ChapterTitleEdits ?? new Dictionary<int, string>())
    {
      if (index < 0) diagnostics.Add(new(EditCopyCodes.ChapterIndexInvalid, $"Chapter {index + 1} does not exist."));
      else if (string.IsNullOrWhiteSpace(title) || title.Contains('\0')) diagnostics.Add(new(EditCopyCodes.ChapterTitleInvalid, $"Chapter {index + 1} needs a title."));
    }
    if (request.Profile is null) diagnostics.Add(new(EditCopyCodes.TagNotInProfile, "A metadata profile is required."));
    if (!TryFullPath(request.DestinationPath, out var destination) || Path.GetFileName(destination).Length == 0 || fileSystem.DirectoryExists(destination))
    {
      diagnostics.Add(new(EditCopyCodes.DestinationInvalid, "The destination is not a file path."));
      return diagnostics;
    }
    if (IsSameFile(source, destination, fileSystem))
      diagnostics.Add(new(EditCopyCodes.DestinationIsSource, "The destination is the file being edited. Choose a different name or folder."));
    return diagnostics;
  }

  // Equal paths, or an existing destination that is the same physical file (a hard link, or a path through a link).
  // When the destination exists and its identity can't be read, it is treated as the same file.
  public static bool IsSameFile(string source, string destination, IEditCopyFileSystem fileSystem)
  {
    var fullSource = Path.GetFullPath(source);
    var fullDestination = Path.GetFullPath(destination);
    if (string.Equals(fullSource, fullDestination, StringComparison.OrdinalIgnoreCase)) return true;
    if (!fileSystem.FileExists(fullDestination)) return false;
    var sourceId = fileSystem.GetPhysicalId(fullSource);
    var destinationId = fileSystem.GetPhysicalId(fullDestination);
    return sourceId is null || destinationId is null || string.Equals(sourceId, destinationId, StringComparison.OrdinalIgnoreCase);
  }

  private static bool TryFullPath(string? path, out string fullPath)
  {
    fullPath = "";
    if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\0', '\r', '\n']) >= 0 || !Path.IsPathFullyQualified(path)) return false;
    try { fullPath = Path.GetFullPath(path); return true; }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
  }
}
