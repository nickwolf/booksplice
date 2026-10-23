using System.Text.Json;
using System.Text.RegularExpressions;

namespace BookSplice.Core.Metadata;

public sealed record MetadataProfileLoadError(string FileName, string? ProfileId, string Reason);

public sealed partial class MetadataProfileCatalog
{
  public const int MaximumFileBytes = 64 * 1024;
  public const int MaximumKeyLength = 64;
  public const int MaximumVersionLength = 32;
  private static readonly string[] FieldNames = Enum.GetNames<SemanticField>();

  private readonly IReadOnlyList<MetadataProfile> _profiles;
  private MetadataProfileCatalog(IReadOnlyList<MetadataProfile> profiles, IReadOnlyList<MetadataProfileLoadError> errors) => (_profiles, Errors) = (profiles, errors);

  public static MetadataProfileCatalog BuiltIn { get; } = new([MetadataProfiles.GenericMp4], []);
  public IReadOnlyList<MetadataProfile> Profiles => _profiles;
  public IReadOnlyList<MetadataProfileLoadError> Errors { get; }

  public MetadataProfile? Find(string? id) => id is null ? null : _profiles.FirstOrDefault(profile => string.Equals(profile.Name, id, StringComparison.Ordinal));
  public static bool IsBuiltIn(string? id) => id is not null && string.Equals(id, MetadataProfiles.GenericMp4.Name, StringComparison.Ordinal);

  public static string PublicLabel(string? id) => IsBuiltIn(id) ? id! : "Custom";

  public static bool IsValidProfileId(string? id) => id is not null && ProfileIdPattern().IsMatch(id);

  public static MetadataProfileCatalog Load(string profilesDirectory)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(profilesDirectory);
    var errors = new List<MetadataProfileLoadError>();
    var candidates = new List<(string File, MetadataProfile Profile)>();
    string root;
    string[] files;
    try
    {
      if (!Directory.Exists(profilesDirectory)) return BuiltIn;
      root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilesDirectory));
      files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).Where(file => string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    {
      return new([MetadataProfiles.GenericMp4], [new("(profiles folder)", null, "The profiles folder could not be read.")]);
    }

    foreach (var file in files)
    {
      var name = Path.GetFileName(file);
      string? claimed = null;
      try
      {
        var full = Path.GetFullPath(file);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { errors.Add(new(name, null, "The file is outside the profiles folder.")); continue; }
        var info = new FileInfo(full);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { errors.Add(new(name, null, "Links and reparse points are not supported.")); continue; }
        if (info.Length > MaximumFileBytes) { errors.Add(new(name, null, $"The file is larger than {MaximumFileBytes / 1024} KiB.")); continue; }
        byte[] bytes;
        using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
          bytes = new byte[MaximumFileBytes + 1];
          var read = 0;
          int count;
          while (read < bytes.Length && (count = stream.Read(bytes, read, bytes.Length - read)) > 0) read += count;
          if (read > MaximumFileBytes) { errors.Add(new(name, null, $"The file is larger than {MaximumFileBytes / 1024} KiB.")); continue; }
          Array.Resize(ref bytes, read);
        }
        var profile = Parse(bytes, out claimed, out var reason);
        if (profile is null) errors.Add(new(name, claimed, reason!));
        else candidates.Add((name, profile));
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
      {
        errors.Add(new(name, claimed, "The file could not be read."));
      }
    }

    var accepted = new List<MetadataProfile>();
    foreach (var group in candidates.GroupBy(candidate => candidate.Profile.Name, StringComparer.OrdinalIgnoreCase))
    {
      if (string.Equals(group.Key, MetadataProfiles.GenericMp4.Name, StringComparison.OrdinalIgnoreCase))
        foreach (var candidate in group) errors.Add(new(candidate.File, candidate.Profile.Name, "The profile ID matches a built-in profile."));
      else if (group.Count() > 1)
        foreach (var candidate in group) errors.Add(new(candidate.File, candidate.Profile.Name, "Another profile file uses the same profile ID."));
      else accepted.Add(group.Single().Profile);
    }

    accepted.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
    return new([MetadataProfiles.GenericMp4, .. accepted], errors.OrderBy(error => error.FileName, StringComparer.OrdinalIgnoreCase).ToArray());
  }

  private static MetadataProfile? Parse(byte[] bytes, out string? claimedId, out string? reason)
  {
    claimedId = null;
    reason = null;
    JsonDocument document;
    try { document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); }
    catch (JsonException) { reason = "The file is not valid JSON, or it repeats a property name."; return null; }
    using (document)
    {
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object) { reason = "The top level must be a JSON object."; return null; }
      if (root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String && nameElement.GetString() is { } claimed && IsValidProfileId(claimed)) claimedId = claimed;
      foreach (var property in root.EnumerateObject())
        if (property.Name is not ("schemaVersion" or "name" or "version" or "mappings")) { reason = $"Unknown property \"{Display(property.Name)}\"."; return null; }
      if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion)) { reason = "\"schemaVersion\" must be the number 1."; return null; }
      if (schemaVersion != 1) { reason = "Only schemaVersion 1 is supported."; return null; }
      if (!root.TryGetProperty("name", out nameElement) || nameElement.ValueKind != JsonValueKind.String) { reason = "\"name\" must be a string."; return null; }
      var name = nameElement.GetString()!;
      if (!IsValidProfileId(name)) { reason = "\"name\" must be 1 to 64 letters, digits, dots, underscores or hyphens."; return null; }
      if (!root.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.String) { reason = "\"version\" must be a string."; return null; }
      var version = versionElement.GetString()!;
      if (version.Length is 0 or > MaximumVersionLength || version.Any(char.IsControl)) { reason = $"\"version\" must be 1 to {MaximumVersionLength} characters without control characters."; return null; }
      if (!root.TryGetProperty("mappings", out var mappingsElement) || mappingsElement.ValueKind != JsonValueKind.Array) { reason = "\"mappings\" must be a list."; return null; }
      if (mappingsElement.GetArrayLength() == 0) { reason = "\"mappings\" must not be empty."; return null; }

      var mappings = new List<MetadataFieldMapping>();
      var fields = new HashSet<SemanticField>();
      var allKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var mapping in mappingsElement.EnumerateArray())
      {
        if (mapping.ValueKind != JsonValueKind.Object) { reason = "Each mapping must be an object."; return null; }
        foreach (var property in mapping.EnumerateObject())
          if (property.Name is not ("field" or "keys")) { reason = $"Unknown mapping property \"{Display(property.Name)}\"."; return null; }
        if (!mapping.TryGetProperty("field", out var fieldElement) || fieldElement.ValueKind != JsonValueKind.String) { reason = "Each mapping needs a \"field\" string."; return null; }
        var fieldName = fieldElement.GetString()!;
        var index = Array.IndexOf(FieldNames, fieldName);
        if (index < 0) { reason = $"Unknown field \"{Display(fieldName)}\"."; return null; }
        var field = Enum.Parse<SemanticField>(fieldName);
        if (!fields.Add(field)) { reason = $"Field \"{fieldName}\" is mapped more than once."; return null; }
        if (!mapping.TryGetProperty("keys", out var keysElement) || keysElement.ValueKind != JsonValueKind.Array) { reason = $"Mapping for \"{fieldName}\" needs a \"keys\" list."; return null; }
        if (keysElement.GetArrayLength() == 0) { reason = $"Mapping for \"{fieldName}\" has no keys."; return null; }
        var keys = new List<string>();
        foreach (var keyElement in keysElement.EnumerateArray())
        {
          if (keyElement.ValueKind != JsonValueKind.String) { reason = $"Keys for \"{fieldName}\" must be strings."; return null; }
          var key = keyElement.GetString()!;
          if (key.Length is 0 or > MaximumKeyLength || key.Any(char.IsControl) || char.IsWhiteSpace(key[0]) || char.IsWhiteSpace(key[^1])) { reason = $"A key for \"{fieldName}\" must be 1 to {MaximumKeyLength} characters, with no control characters or leading or trailing spaces."; return null; }
          if (!allKeys.Add(MetadataTagKeys.Canonical(key))) { reason = $"The key \"{key}\" is used more than once (keys are compared ignoring case)."; return null; }
          keys.Add(key);
        }
        mappings.Add(new MetadataFieldMapping(field, keys.ToArray()));
      }
      return new MetadataProfile(name, version, mappings);
    }
  }

  private static string Display(string value) => value.Length > 40 ? string.Concat(value.AsSpan(0, 40), "...") : value;

  [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
  private static partial Regex ProfileIdPattern();
}
