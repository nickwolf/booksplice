using System.Runtime.Versioning;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Storage;

[SupportedOSPlatform("windows")]
public sealed class InstallationLayout
{
  public InstallationLayout(string originalApplicationPath, string dataRoot, OwnedDirectory managedRoot,
    OwnedDirectory versions, OwnedDirectory staging, OwnedDirectory trash, OwnedDirectoryStore store)
  {
    ArgumentNullException.ThrowIfNull(store);
    OriginalApplicationPath = Canonical(originalApplicationPath);
    DataRoot = Canonical(dataRoot);
    ManagedRoot = managedRoot ?? throw new ArgumentNullException(nameof(managedRoot));
    Versions = versions ?? throw new ArgumentNullException(nameof(versions));
    Staging = staging ?? throw new ArgumentNullException(nameof(staging));
    Trash = trash ?? throw new ArgumentNullException(nameof(trash));
    using (var original = SafeDirectoryHandle.OpenPath(OriginalApplicationPath, false))
      OriginalIdentity = original.Identity;
    using (var data = SafeDirectoryHandle.OpenPath(DataRoot, false))
      DataIdentity = data.Identity;
    Validate(store);
  }

  public string OriginalApplicationPath { get; }
  public string DataRoot { get; }
  public OwnedDirectory ManagedRoot { get; }
  public OwnedDirectory Versions { get; }
  public OwnedDirectory Staging { get; }
  public OwnedDirectory Trash { get; }
  public Guid InstallationId => ManagedRoot.InstallationId;
  public FileIdentity OriginalIdentity { get; }
  public FileIdentity DataIdentity { get; }
  public string StableLauncherPath => Path.Combine(ManagedRoot.Path, "BookSplice.Launcher.exe");

  public void Validate(OwnedDirectoryStore store)
  {
    ArgumentNullException.ThrowIfNull(store);
    if (InstallationId == Guid.Empty ||
        new[] { Versions, Staging, Trash }.Any(child => child.InstallationId != InstallationId) ||
        ManagedRoot.Kind != "managed-root" || Versions.Kind != "versions" ||
        Staging.Kind != "staging" || Trash.Kind != "trash")
    {
      throw new OwnedDirectoryException("Installation ownership records disagree.");
    }

    var sibling = Path.GetDirectoryName(OriginalApplicationPath)!;
    var expectedRoot = Path.Combine(sibling, ".booksplice-managed-" + InstallationId.ToString("D"));
    if (!Same(ManagedRoot.Path, expectedRoot) ||
        !Same(Versions.Path, Path.Combine(expectedRoot, "versions")) ||
        !Same(Staging.Path, Path.Combine(expectedRoot, "staging")) ||
        !Same(Trash.Path, Path.Combine(expectedRoot, "trash")) ||
        Same(OriginalApplicationPath, ManagedRoot.Path))
    {
      throw new OwnedDirectoryException("Installation paths do not match the fixed layout.");
    }

    using var original = SafeDirectoryHandle.OpenPath(OriginalApplicationPath, false);
    using var data = SafeDirectoryHandle.OpenPath(DataRoot, false);
    if (original.Identity != OriginalIdentity || data.Identity != DataIdentity)
      throw new OwnedDirectoryException("An installation root was replaced.");
    if (original.Identity.VolumeSerial != Check(ManagedRoot, store).VolumeSerial ||
        Check(Versions, store).VolumeSerial != original.Identity.VolumeSerial ||
        Check(Staging, store).VolumeSerial != original.Identity.VolumeSerial ||
        Check(Trash, store).VolumeSerial != original.Identity.VolumeSerial)
    {
      throw new OwnedDirectoryException("Installation directories must be on one volume.");
    }
  }

  private static FileIdentity Check(OwnedDirectory owned, OwnedDirectoryStore store)
  {
    using var handle = store.OpenValidatedReadOnly(owned);
    return handle.Identity;
  }

  private static bool Same(string left, string right) =>
    string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

  private static string Canonical(string path)
  {
    ArgumentNullException.ThrowIfNull(path);
    var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    if (!Path.IsPathFullyQualified(path) || !Same(path, full))
      throw new OwnedDirectoryException("Installation paths must be canonical and absolute.");
    return full;
  }
}
