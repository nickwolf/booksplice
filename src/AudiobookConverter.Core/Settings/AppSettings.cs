using System.Text.Json;
using AudiobookConverter.Core.Naming;
using AudiobookConverter.Core.Planning;

namespace AudiobookConverter.Core.Settings;

public enum ValidationLevel { Lightweight, Full }
public enum LogLevel { Trace, Debug, Information, Warning, Error }

public sealed record AppSettings(
  int SchemaVersion,
  string OutputDirectory,
  string QualityProfileId,
  ChannelPolicy ChannelPolicy,
  bool CreateChapters,
  int? ConversionJobs,
  CollisionPolicy CollisionPolicy,
  string MetadataProfileId,
  ValidationLevel ValidationLevel,
  LogLevel LogLevel)
{
  internal IReadOnlyDictionary<string, JsonElement> UnknownProperties { get; init; } =
    new Dictionary<string, JsonElement>(StringComparer.Ordinal);

  public const int CurrentSchemaVersion = 1;
  public static AppSettings Defaults { get; } = new(1, string.Empty, "high-quality", ChannelPolicy.PreserveSourceChannels, true, null, CollisionPolicy.AvoidCollision, "GenericMp4", ValidationLevel.Lightweight, LogLevel.Information);
}
