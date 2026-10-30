using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Presets;
using BookSplice.Core.Settings;

namespace BookSplice.Cli;

public sealed record CliConfigurationResult(AppSettings? Settings, QualityProfile? QualityProfile, IReadOnlyList<CliDiagnostic> Diagnostics)
{
  public bool IsSuccess => Settings is not null && QualityProfile is not null && Diagnostics.Count == 0;
}

public static class CliConfiguration
{
  public static CliConfigurationResult Resolve(CliOptions options, SettingsLoadResult loaded, MetadataProfileCatalog? catalog = null, PresetLoadResult? presets = null)
  {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(loaded);
    if (loaded.Code is not (SettingsLoadCode.Valid or SettingsLoadCode.Migrated or SettingsLoadCode.Missing) || loaded.Settings is null)
      return Failure("configuration.settings-invalid", "The saved settings could not be loaded.");

    catalog ??= MetadataProfileCatalog.BuiltIn;
    var defaults = loaded.Settings;
    if (options.PresetName is { } presetName)
    {
      var applied = ApplyPreset(defaults, presetName, presets, catalog);
      if (applied.Settings is null) return new(null, null, applied.Diagnostics);
      defaults = applied.Settings;
    }

    var output = options.OutputDirectory ?? defaults.OutputDirectory;
    if (string.IsNullOrWhiteSpace(output)) return Failure("configuration.output-required", "An output directory is required.");
    if (!Path.IsPathFullyQualified(output) || output.Any(char.IsControl)) return Failure("configuration.output-invalid", "The output directory must be a safe fully qualified path.");

    var profileId = options.QualityProfileId ?? defaults.QualityProfileId;
    var profile = QualityProfileCatalog.FindById(profileId);
    if (profile is null) return Failure("configuration.quality-profile", "The selected quality profile is unavailable.");
    if (options.BitrateKbps is { } bitrate) profile = profile with { Id = "custom", DisplayName = "Custom", AudioBitrateKbps = bitrate };

    var metadataProfileId = options.MetadataProfileId ?? defaults.MetadataProfileId;
    if (catalog.Find(metadataProfileId) is null) return Failure("configuration.metadata-profile-unavailable", DescribeMissingProfile(metadataProfileId, catalog));

    var settings = defaults with
    {
      OutputDirectory = Path.GetFullPath(output),
      MetadataProfileId = metadataProfileId,
      ValidationLevel = options.Validation ?? defaults.ValidationLevel,
      ChannelPolicy = options.ChannelPolicy ?? defaults.ChannelPolicy,
      QualityProfileId = profile.Id,
      ConversionJobs = options.Jobs ?? defaults.ConversionJobs,
      CreateChapters = options.CreateChapters ?? defaults.CreateChapters,
      OutputTemplate = options.OutputTemplate ?? defaults.OutputTemplate,
      CollisionPolicy = options.Overwrite ? CollisionPolicy.Overwrite : CollisionPolicy.AvoidCollision,
    };
    return new(settings, profile, []);
  }

  private static (AppSettings? Settings, IReadOnlyList<CliDiagnostic> Diagnostics) ApplyPreset(AppSettings defaults, string name, PresetLoadResult? presets, MetadataProfileCatalog catalog)
  {
    if (presets is null) return (null, [new("configuration.presets-unavailable", "Presets are not available.")]);
    var problem = presets.HasProblem ? $" The presets file needs fixing: {string.Join(" ", presets.Errors)}" : "";
    if (presets.Find(name) is not { } preset)
    {
      var known = presets.Presets.Count > 0 ? $"Saved presets: {string.Join(", ", presets.Presets.Select(value => value.Name))}." : presets.HasProblem ? "" : "No presets are saved.";
      return (null, [new("configuration.preset-unknown", $"No usable preset named '{name}'. {known}{problem}")]);
    }
    var result = PresetApplier.Apply(defaults, preset, catalog);
    return result.IsSuccess
      ? (result.Settings, [])
      : (null, result.Errors.Select(error => new CliDiagnostic("configuration.preset-invalid", $"{error} Fix the preset in the BookSplice window or edit presets.json.")).ToArray());
  }

  private static string DescribeMissingProfile(string id, MetadataProfileCatalog catalog)
  {
    var text = new System.Text.StringBuilder(FormattableString.Invariant($"The metadata profile '{id}' is not available. "));
    var rejected = catalog.Errors.FirstOrDefault(error => string.Equals(error.ProfileId, id, StringComparison.OrdinalIgnoreCase));
    text.Append(rejected is null ? "No such profile is installed." : FormattableString.Invariant($"The file {rejected.FileName} was rejected: {rejected.Reason}"));
    text.Append(FormattableString.Invariant($" Available profiles: {string.Join(", ", catalog.Profiles.Select(profile => profile.Name))}."));
    text.Append(" Run 'booksplice --help' to see the profiles folder.");
    return text.ToString();
  }

  private static CliConfigurationResult Failure(string code, string message) => new(null, null, [new(code, message)]);
}
