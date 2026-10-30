using System.Text.Json.Serialization;
using BookSplice.Core.Analysis;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Naming;
using BookSplice.Core.Ordering;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;

namespace BookSplice.Core.Queue;

public enum QueueItemState { NotConverted, Succeeded, Failed, Cancelled, Interrupted }

/// <summary>The saved queue. Optional members may be absent from a hand-written or older file.</summary>
public sealed record QueueSnapshot([property: JsonRequired] int SchemaVersion, [property: JsonRequired] DateTime SavedUtc, [property: JsonRequired] IReadOnlyList<QueueItemRecord> Items)
{
  public const int CurrentSchemaVersion = 1;
}

public sealed record SavedSelection([property: JsonRequired] IReadOnlyList<string> Paths, [property: JsonRequired] string RootPath, bool SharesRoot = true);

/// <summary>The per-book fields of <see cref="AppSettings"/>. Everything else comes from the current global settings.</summary>
public sealed record SavedItemSettings(
  [property: JsonRequired] string OutputDirectory,
  [property: JsonRequired] string QualityProfileId,
  [property: JsonRequired] ChannelPolicy ChannelPolicy,
  [property: JsonRequired] bool CreateChapters,
  [property: JsonRequired] CollisionPolicy CollisionPolicy,
  [property: JsonRequired] string MetadataProfileId,
  [property: JsonRequired] ValidationLevel ValidationLevel,
  [property: JsonRequired] string OutputTemplate)
{
  public static SavedItemSettings From(AppSettings settings) => new(settings.OutputDirectory, settings.QualityProfileId, settings.ChannelPolicy,
    settings.CreateChapters, settings.CollisionPolicy, settings.MetadataProfileId, settings.ValidationLevel, settings.OutputTemplate);

  public AppSettings ApplyTo(AppSettings baseSettings) => baseSettings with
  {
    OutputDirectory = OutputDirectory,
    QualityProfileId = QualityProfileId,
    ChannelPolicy = ChannelPolicy,
    CreateChapters = CreateChapters,
    CollisionPolicy = CollisionPolicy,
    MetadataProfileId = MetadataProfileId,
    ValidationLevel = ValidationLevel,
    OutputTemplate = OutputTemplate,
  };

}

/// <summary>Identifies a cover by where it came from, not by object reference. The hash says whether it is still the same picture.</summary>
public sealed record SavedCover([property: JsonRequired] CoverOrigin Origin, [property: JsonRequired] string SourcePath, [property: JsonRequired] string ContentHash, int? EmbeddedPictureIndex = null);

public sealed record QueueItemRecord(
  [property: JsonRequired] string Source,
  [property: JsonRequired] SavedItemSettings Settings,
  SavedSelection? Selection = null,
  IReadOnlyList<SourceIdentity>? ExpectedSources = null,
  OrderCandidateId? SelectedOrder = null,
  ReviewedTrackOrder? ReviewedOrder = null,
  SavedCover? Cover = null,
  bool OmitCover = false,
  IReadOnlyDictionary<string, string>? ChapterTitles = null,
  IReadOnlyDictionary<string, string>? Metadata = null,
  QueueItemState State = QueueItemState.NotConverted,
  string? PublishedPath = null,
  string? Message = null);
