using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;

namespace BookSplice.Core.EditCopy;

// Turns semantic edits into the profile's tag keys, the same keys a conversion with that profile writes.
public static class EditCopyTagMapping
{
  // Keys stored in numeric MP4 atoms (track and disc numbers, flags, counts). An edit copy only writes text, so a field
  // that maps to one of these can't be edited.
  public static IReadOnlySet<string> UnsupportedKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {
    "track", "disc", "gapless_playback", "compilation", "hd_video", "podcast", "episode_sort", "season_number", "tmpo",
  };

  // False when the profile has no tag for the field or any of its tags is one the edit can't write.
  public static bool CanEdit(MetadataProfile profile, SemanticField field)
  {
    ArgumentNullException.ThrowIfNull(profile);
    var keys = profile.Mappings.Where(mapping => mapping.Field == field).SelectMany(mapping => mapping.Keys).ToArray();
    return keys.Length > 0 && !keys.Any(UnsupportedKeys.Contains);
  }

  public static IReadOnlyDictionary<string, string> Map(MetadataProfile profile, IReadOnlyDictionary<SemanticField, string> edits, out IReadOnlyList<ExecutionDiagnostic> diagnostics)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(edits);
    var errors = new List<ExecutionDiagnostic>();
    var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (field, rawValue) in edits.OrderBy(edit => edit.Key))
    {
      var value = rawValue ?? "";
      var keys = profile.Mappings.Where(mapping => mapping.Field == field).SelectMany(mapping => mapping.Keys).Select(MetadataTagKeys.Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
      if (keys.Length == 0)
      {
        errors.Add(new(EditCopyCodes.TagNotInProfile, $"The {profile.Name} profile has no tag for {EditCopyPreview.Label(field)}."));
        continue;
      }
      if (keys.FirstOrDefault(UnsupportedKeys.Contains) is { } unsupported)
      {
        errors.Add(new(EditCopyCodes.TagUnsupported, $"{EditCopyPreview.Label(field)} is stored as a number ({unsupported}) and can't be edited in a copy."));
        continue;
      }
      if (value.Contains('\0'))
      {
        errors.Add(new(EditCopyCodes.TagValueInvalid, $"{EditCopyPreview.Label(field)} contains a character that can't be saved."));
        continue;
      }
      foreach (var key in keys)
      {
        if (tags.TryGetValue(key, out var existing) && !string.Equals(existing, value, StringComparison.Ordinal))
          errors.Add(new(EditCopyCodes.TagConflict, $"Two edits write different values to the {key} tag."));
        else tags[key] = value;
      }
    }
    diagnostics = errors;
    return tags;
  }
}
