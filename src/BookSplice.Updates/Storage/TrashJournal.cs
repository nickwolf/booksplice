using System.Runtime.Versioning;

namespace BookSplice.Updates.Storage;

// Durable intent written before an owned directory is renamed into the trash directory.
public sealed record TrashIntent(Guid Id, OwnedDirectory Source, string TrashPath);

[SupportedOSPlatform("windows")]
public sealed record TrashJournal(int SchemaVersion, Guid InstallationId,
  IReadOnlyList<TrashIntent> Items)
{
  public static StateRecordDefinition<TrashJournal> Definition(string filename,
    Guid installationId, string trashPath) => new(filename, journal =>
      journal.SchemaVersion == 1 && journal.InstallationId == installationId &&
      journal.Items is { Count: <= 1000 } &&
      journal.Items.All(item => item is { Source: not null } && item.Id != Guid.Empty &&
        item.Source.Identity.IsValid && item.Source.InstallationId == installationId &&
        RecoveryRecordValidation.Absolute(item.Source.Path) &&
        RecoveryRecordValidation.Absolute(item.TrashPath) &&
        string.Equals(Path.GetDirectoryName(item.TrashPath), trashPath,
          StringComparison.OrdinalIgnoreCase) &&
        SafeZipExtractor.IsSafeComponent(Path.GetFileName(item.TrashPath))) &&
      journal.Items.Select(item => item.TrashPath).Distinct(StringComparer.OrdinalIgnoreCase)
        .Count() == journal.Items.Count, 1024 * 1024);
}
