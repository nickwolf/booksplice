using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;

namespace BookSplice.Core.EditCopy;

// Turns semantic edits into the profile's tag keys, the same keys a conversion with that profile writes.
public static class EditCopyTagMapping
{
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
