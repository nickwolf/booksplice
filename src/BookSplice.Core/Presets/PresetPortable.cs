using BookSplice.Core.Metadata;

namespace BookSplice.Core.Presets;

public enum PresetImportStatus { Imported, Rejected, NameClash }

public sealed record PresetImportItem(string Name, PresetImportStatus Status, ConversionPreset? Preset, IReadOnlyList<string> Messages);

public sealed record PresetImportReport(PresetLoadCode Code, IReadOnlyList<string> FileErrors, IReadOnlyList<PresetImportItem> Items)
{
  public IReadOnlyList<ConversionPreset> Accepted => Items.Where(item => item.Status == PresetImportStatus.Imported).Select(item => item.Preset!).ToArray();
  public bool IsFileUsable => Code is PresetLoadCode.Valid or PresetLoadCode.HasInvalidEntries;

  public IReadOnlyList<string> Describe()
  {
    var lines = new List<string>(FileErrors);
    foreach (var item in Items)
    {
      lines.Add(item.Status switch
      {
        PresetImportStatus.Imported => $"Imported '{item.Name}'.",
        PresetImportStatus.NameClash => $"Skipped '{item.Name}': a preset with that name already exists. Rename one of them and import again.",
        _ => $"Skipped '{item.Name}'. {string.Join(" ", item.Messages)}",
      });
    }
    return lines;
  }
}

/// <summary>Portable files use the same layout as presets.json and leave out the output folder unless asked.</summary>
public static class PresetPortable
{
  public static string Export(IEnumerable<ConversionPreset> presets, bool includeOutputDirectory)
  {
    ArgumentNullException.ThrowIfNull(presets);
    return PresetFormat.Serialize(presets, includeOutputDirectory);
  }

  public static PresetImportReport Import(byte[] bytes, IEnumerable<ConversionPreset> existing, MetadataProfileCatalog catalog)
  {
    ArgumentNullException.ThrowIfNull(bytes);
    ArgumentNullException.ThrowIfNull(existing);
    ArgumentNullException.ThrowIfNull(catalog);
    var parsed = PresetFormat.Parse(bytes);
    var taken = new HashSet<string>(existing.Select(preset => preset.Name), StringComparer.OrdinalIgnoreCase);
    var items = new List<PresetImportItem>();
    var number = 0;
    foreach (var entry in parsed.Entries)
    {
      number++;
      var name = entry.Name ?? $"entry {number}";
      if (entry.Preset is not { } preset) { items.Add(new(name, PresetImportStatus.Rejected, null, entry.Errors)); continue; }
      var errors = PresetValidator.Validate(preset, catalog);
      if (errors.Count != 0) { items.Add(new(name, PresetImportStatus.Rejected, null, errors)); continue; }
      if (!taken.Add(preset.Name)) { items.Add(new(name, PresetImportStatus.NameClash, null, [])); continue; }
      items.Add(new(name, PresetImportStatus.Imported, preset, []));
    }
    var fileErrors = parsed.Entries.Count == 0 ? parsed.Errors : [];
    return new(parsed.Code, fileErrors, items);
  }
}
