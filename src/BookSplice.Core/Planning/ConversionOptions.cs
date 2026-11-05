using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Settings;

namespace BookSplice.Core.Planning;

public sealed record MetadataEdit(bool IsSet, string? Value)
{
  public static MetadataEdit Set(string value) => new(true, value);
  public static MetadataEdit Clear() => new(true, null);
}

public sealed class ConversionOptions
{
  public ConversionOptions(AppSettings settings, QualityProfile qualityProfile, IReadOnlyDictionary<SemanticField, MetadataEdit>? metadataEdits = null, string? destinationDirectory = null, CollisionPolicy? collisionPolicy = null, string? selectedCoverHash = null, IReadOnlyDictionary<string, string>? chapterTitles = null, bool omitCover = false, IReadOnlyCollection<string>? queuedOutputPaths = null)
  {
    Settings = settings;
    QualityProfile = qualityProfile;
    MetadataEdits = new Dictionary<SemanticField, MetadataEdit>(metadataEdits ?? new Dictionary<SemanticField, MetadataEdit>(), EqualityComparer<SemanticField>.Default);
    DestinationDirectory = destinationDirectory ?? settings.OutputDirectory;
    CollisionPolicy = collisionPolicy ?? settings.CollisionPolicy;
    SelectedCoverHash = selectedCoverHash;
    ChapterTitles = new Dictionary<string, string>(chapterTitles ?? new Dictionary<string, string>(), StringComparer.Ordinal);
    OmitCover = omitCover;
    QueuedOutputPaths = queuedOutputPaths?.ToArray() ?? [];
  }
  public AppSettings Settings { get; }
  public QualityProfile QualityProfile { get; }
  public IReadOnlyDictionary<SemanticField, MetadataEdit> MetadataEdits { get; }
  public string DestinationDirectory { get; }
  public CollisionPolicy CollisionPolicy { get; }
  public IReadOnlyDictionary<string, string> ChapterTitles { get; }
  public bool OmitCover { get; }
  public string? SelectedCoverHash { get; }
  // Output paths other queued books have claimed. The planner adds them to the files already in the destination folder.
  public IReadOnlyCollection<string> QueuedOutputPaths { get; }

  public ConversionOptions WithQueuedOutputPaths(IReadOnlyCollection<string> paths)
    => new(Settings, QualityProfile, MetadataEdits, DestinationDirectory, CollisionPolicy, SelectedCoverHash, ChapterTitles, OmitCover, paths);
}
