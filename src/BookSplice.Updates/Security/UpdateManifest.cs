namespace BookSplice.Updates.Security;

public sealed class VerifiedManifest
{
  internal VerifiedManifest(int schemaVersion, string product, string repository, string tag, string version,
    string target, string archiveName, long archiveLength, string archiveSha256, int packageProtocol,
    int settingsSchemaMin, int settingsSchemaMax, Policy.ReleaseIdentity releaseIdentity,
    string? manifestSha256 = null)
  {
    SchemaVersion = schemaVersion;
    Product = product;
    Repository = repository;
    Tag = tag;
    Version = version;
    Target = target;
    ArchiveName = archiveName;
    ArchiveLength = archiveLength;
    ArchiveSha256 = archiveSha256;
    PackageProtocol = packageProtocol;
    SettingsSchemaMin = settingsSchemaMin;
    SettingsSchemaMax = settingsSchemaMax;
    ReleaseIdentity = releaseIdentity;
    ManifestSha256 = manifestSha256;
  }

  public int SchemaVersion { get; }
  public string Product { get; }
  public string Repository { get; }
  public string Tag { get; }
  public string Version { get; }
  public string Target { get; }
  public string ArchiveName { get; }
  public long ArchiveLength { get; }
  public string ArchiveSha256 { get; }
  public string? ManifestSha256 { get; }
  public int PackageProtocol { get; }
  public int SettingsSchemaMin { get; }
  public int SettingsSchemaMax { get; }
  internal Policy.ReleaseIdentity ReleaseIdentity { get; }
}
