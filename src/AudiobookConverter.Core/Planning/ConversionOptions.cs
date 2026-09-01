using AudiobookConverter.Core.Metadata;
using AudiobookConverter.Core.Naming;
using AudiobookConverter.Core.Settings;

namespace AudiobookConverter.Core.Planning;

public sealed record MetadataEdit(bool IsSet, string? Value)
{
  public static MetadataEdit Set(string value) => new(true, value);
  public static MetadataEdit Clear() => new(true, null);
}

public sealed class ConversionOptions
{
  public ConversionOptions(AppSettings settings, QualityProfile qualityProfile, IReadOnlyDictionary<SemanticField, MetadataEdit>? metadataEdits = null, string? destinationDirectory = null, CollisionPolicy? collisionPolicy = null, string? selectedCoverHash = null)
  {
    Settings = settings;
    QualityProfile = qualityProfile;
    MetadataEdits = new Dictionary<SemanticField, MetadataEdit>(metadataEdits ?? new Dictionary<SemanticField, MetadataEdit>(), EqualityComparer<SemanticField>.Default);
    DestinationDirectory = destinationDirectory ?? settings.OutputDirectory;
    CollisionPolicy = collisionPolicy ?? settings.CollisionPolicy;
    SelectedCoverHash = selectedCoverHash;
  }
  public AppSettings Settings { get; }
  public QualityProfile QualityProfile { get; }
  public IReadOnlyDictionary<SemanticField, MetadataEdit> MetadataEdits { get; }
  public string DestinationDirectory { get; }
  public CollisionPolicy CollisionPolicy { get; }
  public string? SelectedCoverHash { get; }
}
