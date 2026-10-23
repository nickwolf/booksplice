using System.ComponentModel;
using System.Runtime.Versioning;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Storage;

public sealed record CleanupItem(string Path, string Reason);

public sealed record CleanupReport(IReadOnlyList<string> Removed, IReadOnlyList<CleanupItem> Deferred,
  IReadOnlyList<CleanupItem> Refused, bool Skipped);

// Discards superseded versions and finished staging directories. Every deletion starts from an
// exact ownership record: a durable intent, a no-replace rename into trash, an identity check at
// the trash path, then deletion of that one identity. Records are kept until the work is done.
[SupportedOSPlatform("windows")]
public sealed class UpdateCleanup(AtomicStateStore state, UpdateJournal journal,
  OwnedDirectoryStore directories, IActivityLeaseStore activity)
{
  internal Func<string> TrashName { get; init; } = () => "t-" + Guid.NewGuid().ToString("N");

  private readonly List<string> _removed = [];
  private readonly List<CleanupItem> _deferred = [];
  private readonly List<CleanupItem> _refused = [];
  private InstallationLayout _layout = null!;
  private string _ownershipPath = null!;
  private string _trashPath = null!;

  public CleanupReport Run(InstallationLayout layout)
  {
    ArgumentNullException.ThrowIfNull(layout);
    _layout = layout;
    _ownershipPath = Path.Combine(layout.Records.Path, "ownership.json");
    _trashPath = Path.Combine(layout.Records.Path, "trash.json");
    _removed.Clear();
    _deferred.Clear();
    _refused.Clear();
    using var admission = activity.TryEnterUpdate();
    if (admission is null) return new CleanupReport([], [], [], true);
    try
    {
      layout.Validate(directories);
      var (ownership, trash, current, transaction) = Load();
      foreach (var intent in trash.Items) Resume(intent, ownership);
      if (transaction is not null && !transaction.IsFinished()) return Report();
      (ownership, trash, _, _) = Load();
      Discard(ownership, trash, current);
    }
    catch (Exception error) when (error is IOException or InvalidDataException or
      UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
    {
      _refused.Add(new CleanupItem(layout.Records.Path, "Cleanup state is unusable: " + error.Message));
    }
    return Report();
  }

  private CleanupReport Report() => new(_removed.ToArray(), _deferred.ToArray(), _refused.ToArray(), false);

  private (OwnershipState Ownership, TrashJournal Trash, CurrentTarget Current,
    UpdateTransaction? Transaction) Load()
  {
    var ownership = state.Read<OwnershipState>(_ownershipPath);
    var trash = state.Read<TrashJournal>(_trashPath);
    var current = state.Read<CurrentTarget>(Path.Combine(_layout.Records.Path, "current.json"));
    var transaction = journal.Read();
    if (ownership.Kind != StateReadKind.Valid || trash.Kind == StateReadKind.Invalid ||
        current.Kind != StateReadKind.Valid || transaction.Kind == StateReadKind.Invalid)
      throw new InvalidDataException("An ownership, trash, launch or journal record is missing or corrupt.");
    return (ownership.Value!, trash.Value ?? new TrashJournal(1, _layout.InstallationId, []),
      current.Value!, transaction.Value);
  }

  private void Discard(OwnershipState ownership, TrashJournal trash, CurrentTarget current)
  {
    var owned = ownership.Entries.Select(entry => new OwnedDirectory(entry.Path, entry.Identity,
      entry.InstallationId, entry.OperationId, entry.Kind)).ToList();
    var kept = new List<string> { _layout.OriginalApplicationPath, _layout.StableLauncherPath };
    kept.AddRange(new[] { current.Current, current.Previous }
      .Select(target => target?.OwnedDirectory?.Path).OfType<string>());
    kept.AddRange(trash.Items.Select(item => item.Source.Path));
    foreach (var directory in RetentionPolicy.Select(owned, new ProtectedTargets(kept)))
      Discard(directory, owned);
  }

  private void Discard(OwnedDirectory directory, List<OwnedDirectory> owned)
  {
    if (!Directory.Exists(directory.Path))
    {
      RemoveOwnership(directory);
      return;
    }
    var parent = ParentOf(directory, owned);
    if (parent is null)
    {
      _refused.Add(new CleanupItem(directory.Path, "The parent directory is not owned."));
      return;
    }
    var trashPath = Path.Combine(_layout.Trash.Path, TrashName());
    var intent = new TrashIntent(Guid.NewGuid(), directory, trashPath);
    var trashed = new OwnedDirectory(trashPath, directory.Identity, directory.InstallationId,
      directory.OperationId, directory.Kind);
    AddIntent(intent);
    try
    {
      using (var source = directories.OpenValidatedForRename(directory, parent))
      using (var destination = directories.OpenValidatedForDestination(_layout.Trash))
      {
        SafeDirectoryHandle.RejectReparseFile(destination.Handle);
        source.RenameChildTo(destination, Path.GetFileName(trashPath));
      }
    }
    catch (OwnedDirectoryException error)
    {
      RemoveIntent(intent);
      _refused.Add(new CleanupItem(directory.Path, error.Message));
      return;
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
    {
      RemoveIntent(intent);
      _deferred.Add(new CleanupItem(directory.Path, error.Message));
      return;
    }
    DeleteTrashed(intent, trashed);
  }

  private OwnedDirectory? ParentOf(OwnedDirectory directory, List<OwnedDirectory> owned)
  {
    var parentPath = Path.GetDirectoryName(directory.Path);
    foreach (var candidate in new[] { _layout.Versions, _layout.Staging }.Concat(owned))
      if (string.Equals(candidate.Path, parentPath, StringComparison.OrdinalIgnoreCase))
        return candidate;
    return null;
  }

  private void Resume(TrashIntent intent, OwnershipState ownership)
  {
    var trashed = new OwnedDirectory(intent.TrashPath, intent.Source.Identity,
      intent.Source.InstallationId, intent.Source.OperationId, intent.Source.Kind);
    if (Directory.Exists(intent.TrashPath))
    {
      if (ownership.Entries.Any(entry => entry == OwnershipEntry.FromCreated(intent.Source)))
        DeleteTrashed(intent, trashed);
      else
        _refused.Add(new CleanupItem(intent.TrashPath, "The trashed directory has no ownership record."));
    }
    else if (Directory.Exists(intent.Source.Path))
      RemoveIntent(intent);
    else
    {
      RemoveOwnership(intent.Source);
      RemoveIntent(intent);
    }
  }

  private void DeleteTrashed(TrashIntent intent, OwnedDirectory trashed)
  {
    try
    {
      using (var handle = directories.OpenValidatedForRename(trashed, _layout.Trash))
      {
        foreach (var entry in Directory.EnumerateFileSystemEntries(trashed.Path)) DeleteEntry(entry);
        handle.MarkForDeletion();
      }
    }
    catch (OwnedDirectoryException error)
    {
      _refused.Add(new CleanupItem(trashed.Path, error.Message));
      return;
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
    {
      _deferred.Add(new CleanupItem(trashed.Path, error.Message));
      return;
    }
    if (Directory.Exists(trashed.Path))
    {
      _deferred.Add(new CleanupItem(trashed.Path, "The directory is still present."));
      return;
    }
    RemoveOwnership(intent.Source);
    RemoveIntent(intent);
    _removed.Add(intent.Source.Path);
  }

  // A link inside the directory is unlinked, never followed.
  private static void DeleteEntry(string path)
  {
    var attributes = File.GetAttributes(path);
    if ((attributes & FileAttributes.Directory) == 0) File.Delete(path);
    else if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(path, false);
    else Directory.Delete(path, true);
  }

  private void AddIntent(TrashIntent intent) => state.Transition<TrashJournal>(_trashPath, read =>
  {
    if (read.Kind == StateReadKind.Invalid) throw new InvalidDataException("The trash journal is invalid.");
    var items = read.Value?.Items.ToList() ?? [];
    items.Add(intent);
    return new TrashJournal(1, _layout.InstallationId, items);
  });

  private void RemoveIntent(TrashIntent intent) => state.Transition<TrashJournal>(_trashPath, read =>
    new TrashJournal(1, _layout.InstallationId,
      (read.Value?.Items ?? []).Where(item => item.Id != intent.Id).ToArray()));

  private void RemoveOwnership(OwnedDirectory directory) =>
    state.Transition<OwnershipState>(_ownershipPath, read =>
    {
      if (read.Kind != StateReadKind.Valid) throw new InvalidDataException("The ownership state is invalid.");
      var entry = OwnershipEntry.FromCreated(directory);
      return read.Value! with { Entries = read.Value.Entries.Where(item => item != entry).ToArray() };
    });
}
