using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;

namespace BookSplice.Core.Presets;

/// <summary>A named group of conversion settings. A null OutputTemplate or OutputDirectory leaves the job's own value alone.</summary>
public sealed record ConversionPreset(
  string Name,
  string QualityProfileId,
  ChannelPolicy ChannelPolicy,
  bool CreateChapters,
  ValidationLevel ValidationLevel,
  string MetadataProfileId,
  string? OutputTemplate = null,
  string? OutputDirectory = null)
{
  public static ConversionPreset FromSettings(string name, AppSettings settings, bool includeOutputDirectory = false, bool includeOutputTemplate = true)
  {
    ArgumentNullException.ThrowIfNull(settings);
    return new(name.Trim(), settings.QualityProfileId, settings.ChannelPolicy, settings.CreateChapters, settings.ValidationLevel, settings.MetadataProfileId,
      includeOutputTemplate ? settings.OutputTemplate : null, includeOutputDirectory ? settings.OutputDirectory : null);
  }
}

public static class PresetNames
{
  public const int MaximumLength = 64;

  public static string? Check(string? name)
  {
    var trimmed = name?.Trim();
    if (string.IsNullOrEmpty(trimmed)) return "The preset name is empty.";
    if (trimmed.Length > MaximumLength) return $"The preset name is longer than {MaximumLength} characters.";
    if (trimmed.Any(char.IsControl)) return "The preset name contains control characters.";
    return null;
  }

  public static bool Same(string? left, string? right) => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}

public static class PresetValidator
{
  public static IReadOnlyList<string> Validate(ConversionPreset preset, MetadataProfileCatalog catalog)
  {
    ArgumentNullException.ThrowIfNull(preset);
    ArgumentNullException.ThrowIfNull(catalog);
    var errors = new List<string>();
    var label = $"Preset '{preset.Name}'";
    if (PresetNames.Check(preset.Name) is { } nameError) errors.Add($"{label}: name: {nameError}");
    if (QualityProfileCatalog.FindById(preset.QualityProfileId) is null)
      errors.Add($"{label}: qualityProfileId '{preset.QualityProfileId}' is not a known quality profile. Use {string.Join(", ", QualityProfileCatalog.Version1.Select(profile => profile.Id))}.");
    if (!Enum.IsDefined(preset.ChannelPolicy)) errors.Add($"{label}: channelPolicy is not valid.");
    if (!Enum.IsDefined(preset.ValidationLevel)) errors.Add($"{label}: validationLevel is not valid.");
    if (catalog.Find(preset.MetadataProfileId) is null)
      errors.Add($"{label}: metadataProfileId '{preset.MetadataProfileId}' is not an available metadata profile. Available: {string.Join(", ", catalog.Profiles.Select(profile => profile.Name))}.");
    if (preset.OutputTemplate is not null && OutputPathTemplate.Validate(preset.OutputTemplate) is { } templateError)
      errors.Add($"{label}: outputTemplate: {templateError}");
    if (preset.OutputDirectory is { } directory && (directory.Any(char.IsControl) || !Path.IsPathFullyQualified(directory)))
      errors.Add($"{label}: outputDirectory must be a fully qualified path without control characters.");
    return errors;
  }
}

public sealed record PresetApplyResult(AppSettings? Settings, IReadOnlyList<string> Errors)
{
  public bool IsSuccess => Settings is not null && Errors.Count == 0;
}

public static class PresetApplier
{
  /// <summary>Copies the preset's values onto the settings. Nothing is applied when any value is invalid.</summary>
  public static PresetApplyResult Apply(AppSettings settings, ConversionPreset preset, MetadataProfileCatalog catalog)
  {
    ArgumentNullException.ThrowIfNull(settings);
    var errors = PresetValidator.Validate(preset, catalog);
    if (errors.Count != 0) return new(null, errors);
    return new(settings with
    {
      QualityProfileId = preset.QualityProfileId,
      ChannelPolicy = preset.ChannelPolicy,
      CreateChapters = preset.CreateChapters,
      ValidationLevel = preset.ValidationLevel,
      MetadataProfileId = preset.MetadataProfileId,
      OutputTemplate = preset.OutputTemplate ?? settings.OutputTemplate,
      OutputDirectory = preset.OutputDirectory ?? settings.OutputDirectory,
    }, []);
  }
}
