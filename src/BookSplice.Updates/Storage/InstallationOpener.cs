using System.Runtime.Versioning;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Storage;

[SupportedOSPlatform("windows")]
public sealed record OpenedInstallation(InstallationLayout Layout, InstallationRecord Record,
  AtomicStateStore State, OwnedDirectoryStore Directories, UpdateJournal Journal)
{
  public string RecordsPath => Layout.Records.Path;
}

// Opens or creates an installation. Callers that only launch pass canCreate = false so the
// returned store refuses to create directories.
[SupportedOSPlatform("windows")]
public static class InstallationOpener
{
  private const string Prefix = ".booksplice-managed-";

  public static OpenedInstallation Open(string managedRootPath, bool canCreate = false,
    string? programsDirectory = null)
  {
    var rootName = Path.GetFileName(managedRootPath);
    if (!rootName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ||
        !Guid.TryParseExact(rootName[Prefix.Length..], "D", out var installationId))
      throw new InvalidDataException("The path is outside a recorded installation.");
    using var rootHandle = SafeDirectoryHandle.OpenPath(managedRootPath, false);
    using var recordsHandle = SafeDirectoryHandle.OpenGuardedChild(rootHandle, "records");
    var recordsPath = Path.Combine(managedRootPath, "records");
    var managed = new OwnedDirectory(managedRootPath, rootHandle.Identity, installationId,
      Guid.NewGuid(), "managed-root");
    var records = new OwnedDirectory(recordsPath, recordsHandle.Identity, installationId,
      Guid.NewGuid(), "records");
    var state = new AtomicStateStore(records, Definitions(installationId, records, managed,
      Path.Combine(managedRootPath, "BookSplice.Launcher.exe"), programsDirectory));
    var installationRead = state.Read<InstallationRecord>(
      Path.Combine(recordsPath, "installation.json"));
    if (installationRead.Kind != StateReadKind.Valid || installationRead.Value is not { } record ||
        record.ManagedRoot.Path != managedRootPath ||
        record.ManagedRoot.Identity != rootHandle.Identity ||
        record.Records.Path != recordsPath ||
        record.Records.Identity != recordsHandle.Identity ||
        record.StableLauncherIdentity is not { } launcherIdentity ||
        record.StableLauncherSha256 is not { } launcherHash)
      throw new InvalidDataException("The installation record is invalid.");
    using (var image = rootHandle.OpenFile("BookSplice.Launcher.exe"))
    {
      SafeDirectoryHandle.RejectReparseFile(image);
      if (NativeFileIdentity.Read(image) != launcherIdentity ||
          !string.Equals(PackageInventory.HashHandle(image), launcherHash,
            StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The stable launcher image changed.");
    }
    // The directory store walks the parent with write access, so release these guards first.
    rootHandle.Dispose();
    recordsHandle.Dispose();
    var parent = Path.GetDirectoryName(record.OriginalApplicationPath)!;
    DurableOwnershipRecorder? recorder = canCreate ? new DurableOwnershipRecorder(state,
      Path.Combine(recordsPath, "ownership.json"), records, managed) : null;
    var directories = new OwnedDirectoryStore(parent, recorder is null ?
      _ => throw new InvalidOperationException("Launch cannot create directories.") :
      recorder.Record);
    var layout = record.Restore(directories);
    var journal = new UpdateJournal(state, Path.Combine(recordsPath, "journal.json"),
      installationId, recorder);
    return new OpenedInstallation(layout, record, state, directories, journal);
  }

  // Creates the managed root beside the original folder, records it, installs the stable
  // launcher from the original package and binds the original executable last.
  public static OpenedInstallation Create(string originalApplicationPath, string dataRoot,
    string originalVersion, string? programsDirectory = null)
  {
    var parent = Path.GetDirectoryName(originalApplicationPath)!;
    if (!File.Exists(Path.Combine(originalApplicationPath, "BookSplice.Launcher.exe")))
      throw new InvalidDataException("This copy of BookSplice has no update launcher.");
    var id = Guid.NewGuid();
    var store = new OwnedDirectoryStore(parent, _ => { });
    var managed = store.CreateNamedExclusive(parent, Prefix + id.ToString("D"), "managed-root",
      id, Guid.NewGuid());
    var versions = store.CreateNamedExclusive(managed, "versions", "versions", id, Guid.NewGuid());
    var staging = store.CreateNamedExclusive(managed, "staging", "staging", id, Guid.NewGuid());
    var trash = store.CreateNamedExclusive(managed, "trash", "trash", id, Guid.NewGuid());
    var records = store.CreateNamedExclusive(managed, "records", "records", id, Guid.NewGuid());
    var layout = new InstallationLayout(originalApplicationPath, dataRoot, managed, records,
      versions, staging, trash, store);
    var record = InstallationRecord.FromLayout(layout, originalVersion);
    var state = new AtomicStateStore(records, Definitions(id, records, managed,
      layout.StableLauncherPath, programsDirectory));
    var installationPath = Path.Combine(records.Path, "installation.json");
    state.Write(Path.Combine(records.Path, "ownership.json"), new OwnershipState(1, id,
      records.Identity, new[] { managed, records, versions, staging, trash }
        .Select(OwnershipEntry.FromCreated).ToArray()));
    state.Write(installationPath, record);
    state.Write(Path.Combine(records.Path, "current.json"), new CurrentTarget(1, id,
      new OwnedOrOriginalTarget(TargetKind.Original, record.OriginalExecutablePath,
        record.OriginalIdentity, record.OriginalExecutableIdentity, record.OriginalPackage,
        record.OriginalVersion, record.OriginalProtocolVersion, null), null));
    StableLauncherBootstrap.Ensure(layout, JournalSnapshot.Read(state, records.Path), state,
      installationPath);
    var opened = Open(managed.Path, canCreate: true, programsDirectory);
    InstallationRegistry.Open(dataRoot, create: true)!.Bind(new InstallationBinding(
      opened.Record.OriginalExecutablePath, opened.Record.OriginalIdentity,
      opened.Record.OriginalExecutableIdentity, id, TargetKind.Original, managed.Path,
      managed.Identity), opened.Record);
    return opened;
  }

  private static IStateRecordDefinition[] Definitions(Guid id, OwnedDirectory records,
    OwnedDirectory managed, string launcherPath, string? programsDirectory) =>
  [
    InstallationRecord.Definition("installation.json", id),
    CurrentTarget.Definition("current.json", id),
    UpdateTransaction.Definition("journal.json", id),
    OwnershipState.Definition("ownership.json", records, managed),
    ShortcutOwnershipRecord.Definition("shortcut.json", id,
      Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(programsDirectory ??
        Environment.GetFolderPath(Environment.SpecialFolder.Programs))), "BookSplice.lnk"),
      launcherPath)
  ];
}
