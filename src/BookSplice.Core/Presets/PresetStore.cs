using BookSplice.Core.Settings;

namespace BookSplice.Core.Presets;

public sealed record PresetLoadResult(PresetLoadCode Code, IReadOnlyList<ConversionPreset> Presets, IReadOnlyList<string> Errors)
{
  /// <summary>False when the file is unreadable, newer than this version, or has entries that would be lost on save.</summary>
  public bool CanSave => Code is PresetLoadCode.Missing or PresetLoadCode.Valid;
  public bool HasProblem => Code is not (PresetLoadCode.Missing or PresetLoadCode.Valid);
  public ConversionPreset? Find(string? name) => Presets.FirstOrDefault(preset => PresetNames.Same(preset.Name, name));
}

public sealed class PresetStoreException(string message) : Exception(message);

public sealed class PresetStore(string path)
{
  public const string FileName = "presets.json";
  public string Path { get; } = path;

  public static string DefaultPath(string localAppDataRoot) => System.IO.Path.Combine(localAppDataRoot, "BookSplice", FileName);

  public PresetLoadResult Load()
  {
    if (!File.Exists(Path)) return new(PresetLoadCode.Missing, [], []);
    byte[] bytes;
    try { bytes = File.ReadAllBytes(Path); }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    { return new(PresetLoadCode.Corrupt, [], [$"The presets file could not be read: {exception.Message}"]); }
    var parsed = PresetFormat.Parse(bytes);
    return new(parsed.Code, parsed.Entries.Where(entry => entry.Preset is not null).Select(entry => entry.Preset!).ToArray(), parsed.Errors);
  }

  /// <summary>Replaces the saved list. Refuses, leaving the file untouched, when the current file has a problem that a save would erase.</summary>
  public void Save(IReadOnlyList<ConversionPreset> presets)
  {
    ArgumentNullException.ThrowIfNull(presets);
    if (presets.Count > PresetFormat.MaximumPresets) throw new ArgumentException($"At most {PresetFormat.MaximumPresets} presets can be saved.", nameof(presets));
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var preset in presets)
    {
      if (PresetNames.Check(preset.Name) is { } nameError) throw new ArgumentException($"Preset '{preset.Name}': {nameError}", nameof(presets));
      if (preset.Name != preset.Name.Trim()) throw new ArgumentException($"Preset '{preset.Name}': the name has spaces at either end.", nameof(presets));
      if (!seen.Add(preset.Name)) throw new ArgumentException($"Preset '{preset.Name}': the name is used more than once. Names are not case sensitive.", nameof(presets));
      if (!Enum.IsDefined(preset.ChannelPolicy) || !Enum.IsDefined(preset.ValidationLevel)) throw new ArgumentException($"Preset '{preset.Name}': a setting has no valid value.", nameof(presets));
    }
    var current = Load();
    if (!current.CanSave)
      throw new PresetStoreException($"The presets file was left unchanged because it needs fixing first: {string.Join(" ", current.Errors)} File: {Path}");
    WriteTextAtomic(Path, PresetFormat.Serialize(presets, includeOutputDirectory: true));
  }

  /// <summary>Writes through a temp file in the same folder, so a failed write never leaves a half-written target.</summary>
  public static void WriteTextAtomic(string path, string text) => AtomicFile.Write(System.IO.Path.GetFullPath(path), PresetFormat.Encode(text));
}
