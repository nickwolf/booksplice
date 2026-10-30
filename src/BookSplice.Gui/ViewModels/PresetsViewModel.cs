using System.Collections.ObjectModel;
using System.IO;
using BookSplice.Core.Metadata;
using BookSplice.Core.Presets;
using BookSplice.Core.Settings;

namespace BookSplice.Gui.ViewModels;

public sealed class PresetEntryViewModel(ConversionPreset preset, IReadOnlyList<string> problems)
{
  public ConversionPreset Preset { get; } = preset;
  public string Name => Preset.Name;
  public IReadOnlyList<string> Problems { get; } = problems;
  public bool IsValid => Problems.Count == 0;
  public string Display => IsValid ? Name : $"{Name} (needs fixing)";

  public string Details
  {
    get
    {
      var lines = new List<string>
      {
        $"Quality: {Preset.QualityProfileId}",
        $"Channels: {Preset.ChannelPolicy}",
        $"Chapters: {(Preset.CreateChapters ? "on" : "off")}",
        $"Validation: {Preset.ValidationLevel}",
        $"Metadata profile: {Preset.MetadataProfileId}",
        $"Name template: {Preset.OutputTemplate ?? "(leaves the book's template alone)"}",
        $"Output folder: {(Preset.OutputDirectory is null ? "(leaves the book's folder alone)" : "set")}",
      };
      lines.AddRange(Problems.Select(problem => $"Needs fixing: {problem}"));
      return string.Join(Environment.NewLine, lines);
    }
  }
}

/// <summary>Owns the saved presets for the window. Applying a preset copies its values into one book and keeps no link to it.</summary>
public sealed class PresetsViewModel(PresetStore store, MetadataProfileCatalog? profiles = null) : ObservableModel
{
  private readonly MetadataProfileCatalog _profiles = profiles ?? MetadataProfileCatalog.BuiltIn;
  private string _message = "";
  private string _fileProblem = "";
  private bool _canChange = true;

  public ObservableCollection<PresetEntryViewModel> Presets { get; } = [];
  public string Message { get => _message; private set { _message = value; Changed(); } }
  public string FileProblem { get => _fileProblem; private set { _fileProblem = value; Changed(); Changed(nameof(HasFileProblem)); } }
  public bool HasFileProblem => FileProblem.Length > 0;
  public bool CanChange { get => _canChange; private set { _canChange = value; Changed(); } }

  public void Reload()
  {
    var loaded = store.Load();
    Presets.Clear();
    foreach (var preset in loaded.Presets) Presets.Add(new(preset, PresetValidator.Validate(preset, _profiles)));
    FileProblem = loaded.HasProblem ? $"The presets file needs fixing before presets can be changed: {string.Join(" ", loaded.Errors)} File: {store.Path}" : "";
    CanChange = loaded.CanSave;
  }

  public bool Apply(BookQueueItemViewModel? item, string? name)
  {
    if (item is null) return Fail("Select a book first.");
    var entry = Find(name);
    if (entry is null) return Fail("Choose a preset first.");
    var result = item.ApplyPreset(entry.Preset);
    if (!result.IsSuccess) return Fail($"Preset '{entry.Name}' was not applied. {string.Join(" ", result.Errors)}");
    Message = $"Applied preset '{entry.Name}'.";
    return true;
  }

  public bool SaveNew(string name, AppSettings settings, bool includeOutputDirectory = false)
  {
    var preset = ConversionPreset.FromSettings(name, settings, includeOutputDirectory);
    if (Invalid(preset) is { } invalid) return Fail(invalid);
    return Mutate(list =>
    {
      if (list.Any(existing => PresetNames.Same(existing.Name, preset.Name)))
        return $"A preset named '{preset.Name}' already exists. Choose another name, or update that preset from the manager.";
      list.Add(preset);
      return null;
    }, $"Saved preset '{preset.Name}'.");
  }

  public bool UpdateFrom(string name, AppSettings settings, bool includeOutputDirectory = false)
  {
    var existing = Find(name);
    if (existing is null) return Fail("Choose a preset first.");
    var preset = ConversionPreset.FromSettings(existing.Name, settings, includeOutputDirectory);
    if (Invalid(preset) is { } invalid) return Fail(invalid);
    return Mutate(list =>
    {
      list[IndexOf(list, existing.Name)] = preset;
      return null;
    }, $"Updated preset '{preset.Name}'. Books that already used it are unchanged.");
  }

  public bool Rename(string name, string newName)
  {
    var existing = Find(name);
    if (existing is null) return Fail("Choose a preset first.");
    var renamed = existing.Preset with { Name = newName.Trim() };
    if (PresetNames.Check(renamed.Name) is { } nameError) return Fail(nameError);
    return Mutate(list =>
    {
      if (list.Any(other => PresetNames.Same(other.Name, renamed.Name) && !PresetNames.Same(other.Name, existing.Name)))
        return $"A preset named '{renamed.Name}' already exists.";
      list[IndexOf(list, existing.Name)] = renamed;
      return null;
    }, $"Renamed '{existing.Name}' to '{renamed.Name}'. Books that already used it are unchanged.");
  }

  public bool Delete(string name)
  {
    var existing = Find(name);
    if (existing is null) return Fail("Choose a preset first.");
    return Mutate(list =>
    {
      list.RemoveAt(IndexOf(list, existing.Name));
      return null;
    }, $"Deleted preset '{existing.Name}'. Books that already used it are unchanged.");
  }

  /// <summary>The exact text that export writes. Names left empty export every preset.</summary>
  public string BuildExport(IReadOnlyCollection<string>? names, bool includeOutputDirectory)
  {
    var chosen = Presets.Where(entry => names is null || names.Count == 0 || names.Any(name => PresetNames.Same(name, entry.Name))).Select(entry => entry.Preset);
    return PresetPortable.Export(chosen, includeOutputDirectory);
  }

  public bool ExportToFile(string path, IReadOnlyCollection<string>? names, bool includeOutputDirectory)
  {
    try { PresetStore.WriteTextAtomic(path, BuildExport(names, includeOutputDirectory)); }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    { return Fail($"The file was not saved: {exception.Message}"); }
    Message = "Exported presets.";
    return true;
  }

  public PresetImportReport? ImportFile(string path)
  {
    byte[] bytes;
    try
    {
      if (new FileInfo(path).Length > 1024 * 1024) { Fail("That file is too large to be a presets file."); return null; }
      bytes = File.ReadAllBytes(path);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    { Fail($"The file could not be read: {exception.Message}"); return null; }

    var current = store.Load();
    var report = PresetPortable.Import(bytes, current.Presets, _profiles);
    var summary = string.Join(Environment.NewLine, report.Describe());
    if (report.Accepted.Count == 0) { Message = summary.Length == 0 ? "The file has no presets." : summary; return report; }
    if (!current.CanSave) { Fail($"Nothing was imported because the presets file needs fixing first. {string.Join(" ", current.Errors)}"); return report; }
    try { store.Save([.. current.Presets, .. report.Accepted]); }
    catch (Exception exception) when (exception is PresetStoreException or ArgumentException or IOException or UnauthorizedAccessException)
    { Fail($"Nothing was imported. {exception.Message}"); return report; }
    Reload();
    Message = summary;
    return report;
  }

  private string? Invalid(ConversionPreset preset)
  {
    var errors = PresetValidator.Validate(preset, _profiles);
    return errors.Count == 0 ? null : string.Join(" ", errors);
  }

  private PresetEntryViewModel? Find(string? name) => Presets.FirstOrDefault(entry => PresetNames.Same(entry.Name, name));

  private static int IndexOf(List<ConversionPreset> list, string name) => list.FindIndex(preset => PresetNames.Same(preset.Name, name));

  // Reads the file fresh, applies one change, and saves. A refused save leaves the file exactly as it was.
  private bool Mutate(Func<List<ConversionPreset>, string?> change, string success)
  {
    var current = store.Load();
    if (!current.CanSave) { Reload(); return Fail($"The presets file needs fixing first. {string.Join(" ", current.Errors)}"); }
    var list = current.Presets.ToList();
    if (change(list) is { } refusal) return Fail(refusal);
    try { store.Save(list); }
    catch (Exception exception) when (exception is PresetStoreException or ArgumentException or IOException or UnauthorizedAccessException)
    { return Fail(exception.Message); }
    Reload();
    Message = success;
    return true;
  }

  private bool Fail(string message)
  {
    Message = message;
    return false;
  }
}
