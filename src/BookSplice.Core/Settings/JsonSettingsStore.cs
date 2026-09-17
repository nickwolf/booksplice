using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;

namespace BookSplice.Core.Settings;

public enum SettingsLoadCode { Missing, Valid, Migrated, Corrupt, UnsupportedFutureSchema, ValidationFailed }

public sealed record SettingsLoadResult(SettingsLoadCode Code, AppSettings? Settings, IReadOnlyList<string> Errors)
{
  public bool IsUsable => Settings is not null && Code is SettingsLoadCode.Valid or SettingsLoadCode.Migrated;
  public static SettingsLoadResult Missing(AppSettings defaults) => new(SettingsLoadCode.Missing, defaults, []);
}

public interface ISettingsStore
{
  string SettingsPath { get; }
  Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);
  Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public sealed class JsonSettingsStore : ISettingsStore
{
  private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
  public JsonSettingsStore(string localAppDataRoot) => SettingsPath = Path.Combine(localAppDataRoot, "BookSplice", "settings.json");
  public string SettingsPath { get; }

  public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
  {
    if (!File.Exists(SettingsPath)) return SettingsLoadResult.Missing(AppSettings.Defaults);
    try
    {
      await using var stream = File.OpenRead(SettingsPath);
      var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
      if (node is not JsonObject json) return new(SettingsLoadCode.Corrupt, null, ["root must be an object"]);
      var version = ReadInt(json, "schemaVersion");
      if (version is null or < 0) return new(SettingsLoadCode.Corrupt, null, ["schema version is invalid"]);
      if (version > AppSettings.CurrentSchemaVersion) return new(SettingsLoadCode.UnsupportedFutureSchema, null, ["schema version is newer than supported"]);
      var migrated = version == 0;
      if (migrated)
      {
        if (json["outputDirectory"] is null && json["outputPath"] is not null) json["outputDirectory"] = json["outputPath"]!.DeepClone();
        json["schemaVersion"] = AppSettings.CurrentSchemaVersion;
      }
      var settings = Parse(json);
      var validation = SettingsValidator.Validate(settings);
      return validation.IsValid ? new(migrated ? SettingsLoadCode.Migrated : SettingsLoadCode.Valid, settings, []) : new(SettingsLoadCode.ValidationFailed, null, validation.Errors);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or NotSupportedException) { return new(SettingsLoadCode.Corrupt, null, [ex.Message]); }
  }

  public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
  {
    var validation = SettingsValidator.Validate(settings);
    if (!validation.IsValid) throw new ArgumentException(string.Join("; ", validation.Errors), nameof(settings));
    var directory = Path.GetDirectoryName(SettingsPath)!;
    Directory.CreateDirectory(directory);
    var tempPath = Path.Combine(directory, $".{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");
    try
    {
      await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
      {
        var json = ToJson(settings);
        await JsonSerializer.SerializeAsync(stream, json, Options, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
      }
      cancellationToken.ThrowIfCancellationRequested();
      if (File.Exists(SettingsPath)) File.Replace(tempPath, SettingsPath, null);
      else File.Move(tempPath, SettingsPath);
    }
    finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
  }

  private static AppSettings Parse(JsonObject json) => new AppSettings(
    ReadInt(json, "schemaVersion") ?? -1,
    ReadString(json, "outputDirectory") ?? string.Empty,
    ReadString(json, "qualityProfileId") ?? string.Empty,
    ReadEnum<ChannelPolicy>(json, "channelPolicy"),
    ReadBool(json, "createChapters"), ReadNullableInt(json, "conversionJobs"),
    ReadEnum<CollisionPolicy>(json, "collisionPolicy"), ReadString(json, "metadataProfileId") ?? string.Empty,
    ReadEnum<ValidationLevel>(json, "validationLevel"), ReadEnum<LogLevel>(json, "logLevel")) with
  { UnknownProperties = json.Where(p => !Known.Contains(p.Key)).ToDictionary(p => p.Key, p => JsonSerializer.Deserialize<JsonElement>(p.Value!.ToJsonString()), StringComparer.Ordinal) };

  private static JsonObject ToJson(AppSettings s)
  {
    var json = new JsonObject { ["schemaVersion"] = s.SchemaVersion, ["outputDirectory"] = s.OutputDirectory, ["qualityProfileId"] = s.QualityProfileId, ["channelPolicy"] = s.ChannelPolicy.ToString(), ["createChapters"] = s.CreateChapters, ["conversionJobs"] = s.ConversionJobs, ["collisionPolicy"] = s.CollisionPolicy.ToString(), ["metadataProfileId"] = s.MetadataProfileId, ["validationLevel"] = s.ValidationLevel.ToString(), ["logLevel"] = s.LogLevel.ToString() };
    foreach (var pair in s.UnknownProperties) if (!Known.Contains(pair.Key)) json[pair.Key] = JsonNode.Parse(pair.Value.GetRawText());
    return json;
  }
  private static readonly HashSet<string> Known = new(StringComparer.Ordinal) { "schemaVersion", "outputDirectory", "qualityProfileId", "channelPolicy", "createChapters", "conversionJobs", "collisionPolicy", "metadataProfileId", "validationLevel", "logLevel" };
  private static int? ReadInt(JsonObject o, string n) => o[n]?.GetValue<int?>();
  private static int? ReadNullableInt(JsonObject o, string n)
  {
    if (o[n] is null) return null;
    if (o[n] is JsonValue value && value.TryGetValue<int>(out var number)) return number;
    throw new JsonException();
  }
  private static string? ReadString(JsonObject o, string n) => o[n]?.GetValue<string>();
  private static bool ReadBool(JsonObject o, string n) => o[n]?.GetValue<bool>() ?? false;
  private static T ReadEnum<T>(JsonObject o, string n) where T : struct, Enum => Enum.TryParse<T>(ReadString(o, n), false, out var value) ? value : throw new JsonException($"invalid {n}");
}
