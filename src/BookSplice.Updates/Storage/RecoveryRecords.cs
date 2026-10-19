using BookSplice.Updates.Handover;
using BookSplice.Updates.Windows;
using System.Runtime.Versioning;

namespace BookSplice.Updates.Storage;

public enum TargetKind { Original, Managed }

[SupportedOSPlatform("windows")]
public sealed record PersistedPackage(FileIdentity RootIdentity,
  IReadOnlyList<PackageDirectory> Directories, IReadOnlyList<PackageFile> Files, string Sha256)
{
  public static PersistedPackage FromSnapshot(PackageSnapshot snapshot) =>
    new(snapshot.RootIdentity, snapshot.Directories.ToArray(), snapshot.Files.ToArray(), snapshot.Sha256);

  public PackageSnapshot Restore()
  {
    if (!RootIdentity.IsValid || Directories is null || Files is null ||
        Directories.Count + Files.Count > 20_000 ||
        !RecoveryRecordValidation.Hash(Sha256))
      throw new InvalidDataException("The persisted package inventory is invalid.");
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    long totalLength = 0;
    foreach (var directory in Directories)
      if (directory is null || !directory.Identity.IsValid ||
          !RecoveryRecordValidation.Relative(directory.RelativePath) ||
          !seen.Add(directory.RelativePath) || !directoryPaths.Add(directory.RelativePath))
        throw new InvalidDataException("The persisted package directory is invalid.");
    foreach (var file in Files)
      if (file is null || !file.Identity.IsValid || file.Length is < 0 or > 1024L * 1024 * 1024 ||
          !RecoveryRecordValidation.Hash(file.Sha256) ||
          !RecoveryRecordValidation.Relative(file.RelativePath) || !seen.Add(file.RelativePath))
        throw new InvalidDataException("The persisted package file is invalid.");
      else totalLength = checked(totalLength + file.Length);
    if (totalLength > 4L * 1024 * 1024 * 1024)
      throw new InvalidDataException("The persisted package exceeds its expanded limit.");
    foreach (var path in seen)
    {
      var parent = path.LastIndexOf('/');
      if (parent >= 0 && !directoryPaths.Contains(path[..parent]))
        throw new InvalidDataException("The persisted package ancestry is incomplete.");
    }
    var snapshot = new PackageSnapshot(RootIdentity,
      Directories.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray(),
      Files.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray());
    if (!string.Equals(snapshot.Sha256, Sha256, StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("The persisted package digest differs from its inventory.");
    return snapshot;
  }
}

[SupportedOSPlatform("windows")]
public sealed record OwnedOrOriginalTarget(TargetKind Kind, string ExecutablePath,
  FileIdentity DirectoryIdentity, FileIdentity ExecutableIdentity, PersistedPackage? Package,
  string Version, int ProtocolVersion,
  OwnedDirectory? OwnedDirectory)
{
  public bool IsValid()
  {
    if (!RecoveryRecordValidation.Absolute(ExecutablePath) || !DirectoryIdentity.IsValid ||
        !ExecutableIdentity.IsValid || ProtocolVersion != UpdateProtocol.ProtocolVersion ||
        string.IsNullOrWhiteSpace(Version) || Version.Length > 128) return false;
    try
    {
      if (Kind == TargetKind.Original)
      {
        if (OwnedDirectory is not null) return false;
        if (Package is null) return true;
        Package.Restore();
        return Package.RootIdentity == DirectoryIdentity && Package.Files.Any(file =>
          string.Equals(file.RelativePath, "BookSplice.Gui.exe", StringComparison.OrdinalIgnoreCase) &&
          file.Identity == ExecutableIdentity);
      }
      if (Kind != TargetKind.Managed || Package is null || OwnedDirectory is null ||
          OwnedDirectory.Identity != DirectoryIdentity ||
          !RecoveryRecordValidation.Absolute(OwnedDirectory.Path) ||
          !string.Equals(Path.GetDirectoryName(ExecutablePath), OwnedDirectory.Path,
            StringComparison.OrdinalIgnoreCase) ||
          !string.Equals(Path.GetFileName(ExecutablePath), "BookSplice.Gui.exe",
            StringComparison.OrdinalIgnoreCase)) return false;
      Package.Restore();
      return Package.RootIdentity == DirectoryIdentity && Package.Files.Any(file =>
        string.Equals(file.RelativePath, "BookSplice.Gui.exe", StringComparison.OrdinalIgnoreCase) &&
        file.Identity == ExecutableIdentity);
    }
    catch (Exception error) when (error is InvalidDataException or ArgumentException or NotSupportedException)
    { return false; }
  }
}

[SupportedOSPlatform("windows")]
public sealed record CurrentTarget(int SchemaVersion, Guid InstallationId,
  OwnedOrOriginalTarget Current, OwnedOrOriginalTarget? Previous)
{
  public static StateRecordDefinition<CurrentTarget> Definition(string filename, Guid installationId) =>
    new(filename, value => value.SchemaVersion == 1 && value.InstallationId == installationId &&
      value.Current is not null && value.Current.IsValid() &&
      (value.Previous is null || value.Previous.IsValid()) &&
      (value.Previous is null || !string.Equals(value.Current.ExecutablePath,
        value.Previous.ExecutablePath, StringComparison.OrdinalIgnoreCase)), 16 * 1024 * 1024);
}

[SupportedOSPlatform("windows")]
public sealed record InstallationRecord(int SchemaVersion, Guid InstallationId,
  string OriginalApplicationPath, FileIdentity OriginalIdentity, string DataRoot,
  FileIdentity DataIdentity, OwnedDirectory ManagedRoot, OwnedDirectory Versions,
  OwnedDirectory Staging, OwnedDirectory Trash, string StableLauncherPath,
  FileIdentity? StableLauncherIdentity, string OriginalExecutablePath,
  FileIdentity OriginalExecutableIdentity, string OriginalExecutableSha256,
  string OriginalVersion, int OriginalProtocolVersion,
  FileIdentity? OriginalParentIdentity = null, PersistedPackage? OriginalPackage = null,
  string? OriginalPackageBindingSha256 = null, string? StableLauncherSha256 = null)
{
  public static InstallationRecord FromLayout(InstallationLayout layout, string originalVersion)
  {
    using var original = OriginalPackageInventory.Capture(layout.OriginalApplicationPath,
      layout.OriginalIdentity, originalVersion);
    var executable = original.Snapshot.Files.Single(file =>
      string.Equals(file.RelativePath, "BookSplice.Gui.exe", StringComparison.OrdinalIgnoreCase));
    return new(1, layout.InstallationId, layout.OriginalApplicationPath, layout.OriginalIdentity,
      layout.DataRoot, layout.DataIdentity, layout.ManagedRoot, layout.Versions,
      layout.Staging, layout.Trash, layout.StableLauncherPath, null,
      Path.Combine(layout.OriginalApplicationPath, "BookSplice.Gui.exe"),
      executable.Identity, executable.Sha256,
      originalVersion, UpdateProtocol.ProtocolVersion, original.ParentIdentity,
      PersistedPackage.FromSnapshot(original.Snapshot),
      OriginalPackageInventory.BindingDigest(original.ParentIdentity, original.Snapshot));
  }

  public bool IsValid() => SchemaVersion == 1 && InstallationId != Guid.Empty &&
    OriginalIdentity.IsValid && DataIdentity.IsValid && OriginalExecutableIdentity.IsValid &&
    (StableLauncherIdentity is null ? StableLauncherSha256 is null :
      StableLauncherIdentity.Value.IsValid && RecoveryRecordValidation.Hash(StableLauncherSha256)) &&
    RecoveryRecordValidation.Hash(OriginalExecutableSha256) &&
    !string.IsNullOrWhiteSpace(OriginalVersion) && OriginalVersion.Length <= 128 &&
    OriginalProtocolVersion == UpdateProtocol.ProtocolVersion &&
    ValidOriginalPackage() &&
    string.Equals(OriginalExecutablePath,
      Path.Combine(OriginalApplicationPath, "BookSplice.Gui.exe"), StringComparison.OrdinalIgnoreCase) &&
    RecoveryRecordValidation.Absolute(OriginalApplicationPath) &&
    RecoveryRecordValidation.Absolute(DataRoot) &&
    ManagedRoot is not null && Versions is not null && Staging is not null && Trash is not null &&
    new[] { ManagedRoot, Versions, Staging, Trash }.All(x => x.Identity.IsValid &&
      x.InstallationId == InstallationId && RecoveryRecordValidation.Absolute(x.Path)) &&
    ManagedRoot.Kind == "managed-root" && Versions.Kind == "versions" &&
    Staging.Kind == "staging" && Trash.Kind == "trash" &&
    string.Equals(ManagedRoot.Path, Path.Combine(Path.GetDirectoryName(OriginalApplicationPath)!,
      ".booksplice-managed-" + InstallationId.ToString("D")), StringComparison.OrdinalIgnoreCase) &&
    string.Equals(Versions.Path, Path.Combine(ManagedRoot.Path, "versions"), StringComparison.OrdinalIgnoreCase) &&
    string.Equals(Staging.Path, Path.Combine(ManagedRoot.Path, "staging"), StringComparison.OrdinalIgnoreCase) &&
    string.Equals(Trash.Path, Path.Combine(ManagedRoot.Path, "trash"), StringComparison.OrdinalIgnoreCase) &&
    string.Equals(StableLauncherPath, Path.Combine(ManagedRoot.Path, "BookSplice.Launcher.exe"),
      StringComparison.OrdinalIgnoreCase);

  private bool ValidOriginalPackage()
  {
    if (OriginalPackage is null)
      return OriginalParentIdentity is null && OriginalPackageBindingSha256 is null;
    if (OriginalParentIdentity is not { IsValid: true } ||
        OriginalPackage.RootIdentity != OriginalIdentity ||
        !RecoveryRecordValidation.Hash(OriginalPackageBindingSha256)) return false;
    try
    {
      var snapshot = OriginalPackage.Restore();
      if (!string.Equals(OriginalPackageInventory.BindingDigest(
            OriginalParentIdentity.Value, snapshot), OriginalPackageBindingSha256,
          StringComparison.OrdinalIgnoreCase)) return false;
      return OriginalPackage.Files.Any(file =>
        string.Equals(file.RelativePath, "BookSplice.Gui.exe", StringComparison.OrdinalIgnoreCase) &&
        file.Identity == OriginalExecutableIdentity &&
        string.Equals(file.Sha256, OriginalExecutableSha256, StringComparison.OrdinalIgnoreCase));
    }
    catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
    { return false; }
  }

  public InstallationLayout Restore(OwnedDirectoryStore store)
  {
    if (!IsValid()) throw new InvalidDataException("The installation record is invalid.");
    return InstallationLayout.Restore(OriginalApplicationPath, DataRoot, OriginalIdentity,
      DataIdentity, ManagedRoot, Versions, Staging, Trash, store);
  }

  public static StateRecordDefinition<InstallationRecord> Definition(string filename,
    Guid installationId) => new(filename, x => x.InstallationId == installationId && x.IsValid());
}

[SupportedOSPlatform("windows")]
internal static class RecoveryRecordValidation
{
  internal static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

  internal static bool Absolute(string? path)
  {
    if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return false;
    try
    {
      return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), path,
      StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
    { return false; }
  }

  internal static bool Relative(string? path) => path is { Length: > 0 and <= 4096 } &&
    path.Split('/').All(SafeZipExtractor.IsSafeComponent) && !path.Contains('\\');
}
