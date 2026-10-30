using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BookSplice.Core.Metadata;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;

namespace BookSplice.Core.Presets;

public enum PresetLoadCode { Missing, Valid, Corrupt, UnsupportedFutureSchema, HasInvalidEntries }

internal sealed record ParsedPreset(string? Name, ConversionPreset? Preset, IReadOnlyList<string> Errors);

internal sealed record ParsedPresetFile(PresetLoadCode Code, IReadOnlyList<ParsedPreset> Entries, IReadOnlyList<string> Errors);

/// <summary>The versioned presets.json layout, shared by the local file and portable export.</summary>
internal static class PresetFormat
{
  public const int CurrentSchemaVersion = 1;
  public const int MaximumFileBytes = 1024 * 1024;
  public const int MaximumPresets = 200;

  private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

  public static string Serialize(IEnumerable<ConversionPreset> presets, bool includeOutputDirectory)
  {
    var array = new JsonArray();
    foreach (var preset in presets)
    {
      var node = new JsonObject
      {
        ["name"] = preset.Name,
        ["qualityProfileId"] = preset.QualityProfileId,
        ["channelPolicy"] = preset.ChannelPolicy.ToString(),
        ["createChapters"] = preset.CreateChapters,
        ["validationLevel"] = preset.ValidationLevel.ToString(),
        ["metadataProfileId"] = preset.MetadataProfileId,
      };
      if (preset.OutputTemplate is not null) node["outputTemplate"] = preset.OutputTemplate;
      if (includeOutputDirectory && preset.OutputDirectory is not null) node["outputDirectory"] = preset.OutputDirectory;
      array.Add(node);
    }
    var root = new JsonObject { ["schemaVersion"] = CurrentSchemaVersion, ["presets"] = array };
    return root.ToJsonString(WriteOptions) + "\n";
  }

  public static ParsedPresetFile Parse(byte[] bytes)
  {
    if (bytes.Length > MaximumFileBytes) return Fail(PresetLoadCode.Corrupt, $"The file is larger than {MaximumFileBytes} bytes.");
    JsonNode? node;
    try { node = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 16 }); }
    catch (JsonException exception) { return Fail(PresetLoadCode.Corrupt, $"The file is not valid JSON: {exception.Message}"); }
    if (node is not JsonObject root) return Fail(PresetLoadCode.Corrupt, "The file must contain a JSON object.");
    if (root["schemaVersion"] is not JsonValue version || !version.TryGetValue<int>(out var schema)) return Fail(PresetLoadCode.Corrupt, "schemaVersion is missing or not a number.");
    if (schema > CurrentSchemaVersion) return Fail(PresetLoadCode.UnsupportedFutureSchema, $"The file uses preset schema {schema}, which this version of BookSplice does not understand.");
    if (schema < CurrentSchemaVersion) return Fail(PresetLoadCode.Corrupt, $"schemaVersion {schema} is not valid.");
    if (root["presets"] is not JsonArray array) return Fail(PresetLoadCode.Corrupt, "presets is missing or not a list.");
    if (array.Count > MaximumPresets) return Fail(PresetLoadCode.Corrupt, $"The file lists more than {MaximumPresets} presets.");

    var entries = new List<ParsedPreset>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < array.Count; index++)
    {
      var entry = ParseEntry(array[index], index + 1);
      if (entry.Name is not null && entry.Preset is not null && !seen.Add(entry.Name))
        entry = entry with { Preset = null, Errors = [$"Preset '{entry.Name}': the name appears more than once. Names are not case sensitive."] };
      entries.Add(entry);
    }
    var errors = entries.SelectMany(entry => entry.Errors).ToArray();
    return new(errors.Length == 0 ? PresetLoadCode.Valid : PresetLoadCode.HasInvalidEntries, entries, errors);
  }

  private static ParsedPresetFile Fail(PresetLoadCode code, string message) => new(code, [], [message]);

  private static ParsedPreset ParseEntry(JsonNode? node, int number)
  {
    if (node is not JsonObject obj) return new(null, null, [$"Preset entry {number} is not an object."]);
    var errors = new List<string>();
    string? name = null;
    if (obj["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var rawName))
    {
      name = rawName.Trim();
      if (PresetNames.Check(name) is { } nameError) { errors.Add($"Preset entry {number}: name: {nameError}"); name = null; }
    }
    else errors.Add($"Preset entry {number}: name is missing or not text.");
    var label = name is null ? $"Preset entry {number}" : $"Preset '{name}'";

    var quality = RequiredString(obj, "qualityProfileId", label, errors);
    var channels = RequiredEnum<ChannelPolicy>(obj, "channelPolicy", label, errors);
    var chapters = RequiredBool(obj, "createChapters", label, errors);
    var validation = RequiredEnum<ValidationLevel>(obj, "validationLevel", label, errors);
    var metadata = RequiredString(obj, "metadataProfileId", label, errors);
    var template = OptionalString(obj, "outputTemplate", label, errors);
    var directory = OptionalString(obj, "outputDirectory", label, errors);
    if (errors.Count != 0 || name is null) return new(name, null, errors);
    return new(name, new ConversionPreset(name, quality!, channels!.Value, chapters!.Value, validation!.Value, metadata!, template, directory), []);
  }

  private static string? RequiredString(JsonObject obj, string field, string label, List<string> errors)
  {
    if (obj[field] is JsonValue value && value.TryGetValue<string>(out var text)) return text;
    errors.Add($"{label}: {field} is missing or not text.");
    return null;
  }

  private static string? OptionalString(JsonObject obj, string field, string label, List<string> errors)
  {
    if (obj[field] is null) return null;
    if (obj[field] is JsonValue value && value.TryGetValue<string>(out var text)) return text;
    errors.Add($"{label}: {field} must be text.");
    return null;
  }

  private static bool? RequiredBool(JsonObject obj, string field, string label, List<string> errors)
  {
    if (obj[field] is JsonValue value && value.TryGetValue<bool>(out var flag)) return flag;
    errors.Add($"{label}: {field} is missing or not true/false.");
    return null;
  }

  private static T? RequiredEnum<T>(JsonObject obj, string field, string label, List<string> errors) where T : struct, Enum
  {
    var text = RequiredString(obj, field, label, errors);
    if (text is null) return null;
    if (text.Length > 0 && char.IsLetter(text[0]) && Enum.TryParse<T>(text, false, out var parsed) && Enum.IsDefined(parsed)) return parsed;
    errors.Add($"{label}: {field} '{text}' is not valid. Use {string.Join(", ", Enum.GetNames<T>())}.");
    return null;
  }

  public static byte[] Encode(string text) => new UTF8Encoding(false).GetBytes(text);
}
