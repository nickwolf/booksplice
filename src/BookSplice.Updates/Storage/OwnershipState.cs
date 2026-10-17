using BookSplice.Updates.Windows;
using System.Runtime.Versioning;

namespace BookSplice.Updates.Storage;

public sealed record OwnershipEntry(string Path, FileIdentity Identity, Guid InstallationId,
  Guid OperationId, string Kind, string UpdaterCreatedMarker)
{
  public const string Marker = "booksplice-updater-created-v1";

  public static OwnershipEntry FromCreated(OwnedDirectory directory) =>
    new(directory.Path, directory.Identity, directory.InstallationId,
      directory.OperationId, directory.Kind, Marker);
}

[SupportedOSPlatform("windows")]
public sealed record OwnershipState(int SchemaVersion, Guid InstallationId,
  FileIdentity RecordRootIdentity, IReadOnlyList<OwnershipEntry> Entries)
{
  public static StateRecordDefinition<OwnershipState> Definition(string filename,
    OwnedDirectory recordRoot)
  {
    ArgumentNullException.ThrowIfNull(recordRoot);
    var root = recordRoot.Path;
    return new StateRecordDefinition<OwnershipState>(filename, state =>
      state.SchemaVersion == 1 && state.InstallationId == recordRoot.InstallationId &&
      state.RecordRootIdentity == recordRoot.Identity && state.Entries is not null &&
      state.Entries.Count <= 20_000 && state.Entries.All(entry =>
        Valid(entry, root, recordRoot.Identity, state.InstallationId)) &&
      state.Entries.Select(entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
        state.Entries.Count, 16 * 1024 * 1024);
  }

  private static bool Valid(OwnershipEntry entry, string root, FileIdentity rootIdentity,
    Guid installationId)
  {
    if (entry is null || entry.Identity.IsValid == false || entry.InstallationId != installationId ||
        entry.OperationId == Guid.Empty || entry.UpdaterCreatedMarker != OwnershipEntry.Marker ||
        string.IsNullOrEmpty(entry.Kind) || entry.Kind.Length > 40 ||
        !char.IsAsciiLetterOrDigit(entry.Kind[0]) ||
        !entry.Kind.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') ||
        string.IsNullOrEmpty(entry.Path) || !Path.IsPathFullyQualified(entry.Path)) return false;
    try
    {
      var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Path));
      if (!string.Equals(full, entry.Path, StringComparison.OrdinalIgnoreCase)) return false;
      if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        return entry.Kind == "managed-root" && entry.Identity == rootIdentity;
      if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        return false;
      return full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar)
        .All(SafeZipExtractor.IsSafeComponent);
    }
    catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return false;
    }
  }
}
