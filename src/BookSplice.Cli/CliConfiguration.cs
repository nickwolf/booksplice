using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;

namespace BookSplice.Cli;

public sealed record CliConfigurationResult(AppSettings? Settings, QualityProfile? QualityProfile, IReadOnlyList<CliDiagnostic> Diagnostics)
{
  public bool IsSuccess => Settings is not null && QualityProfile is not null && Diagnostics.Count == 0;
}

public static class CliConfiguration
{
  public static CliConfigurationResult Resolve(CliOptions options, SettingsLoadResult loaded, MetadataProfileCatalog? catalog = null)
  {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(loaded);
    if (loaded.Code is not (SettingsLoadCode.Valid or SettingsLoadCode.Migrated or SettingsLoadCode.Missing) || loaded.Settings is null)
      return Failure("configuration.settings-invalid", "The saved settings could not be loaded.");

    var output = options.OutputDirectory ?? loaded.Settings.OutputDirectory;
    if (string.IsNullOrWhiteSpace(output)) return Failure("configuration.output-required", "An output directory is required.");
    if (!Path.IsPathFullyQualified(output) || output.Any(char.IsControl)) return Failure("configuration.output-invalid", "The output directory must be a safe fully qualified path.");

    var profileId = options.QualityProfileId ?? loaded.Settings.QualityProfileId;
    var profile = QualityProfileCatalog.FindById(profileId);
    if (profile is null) return Failure("configuration.quality-profile", "The selected quality profile is unavailable.");
    if (options.BitrateKbps is { } bitrate) profile = profile with { Id = "custom", DisplayName = "Custom", AudioBitrateKbps = bitrate };

    catalog ??= MetadataProfileCatalog.BuiltIn;
    var metadataProfileId = options.MetadataProfileId ?? loaded.Settings.MetadataProfileId;
    if (catalog.Find(metadataProfileId) is null) return Failure("configuration.metadata-profile-unavailable", DescribeMissingProfile(metadataProfileId, catalog));

    var settings = loaded.Settings with
    {
      OutputDirectory = Path.GetFullPath(output),
      MetadataProfileId = metadataProfileId,
      ValidationLevel = options.Validation ?? loaded.Settings.ValidationLevel,
      ChannelPolicy = options.ChannelPolicy ?? loaded.Settings.ChannelPolicy,
      QualityProfileId = profile.Id,
      ConversionJobs = options.Jobs ?? loaded.Settings.ConversionJobs,
      CreateChapters = options.CreateChapters ?? loaded.Settings.CreateChapters,
      OutputTemplate = options.OutputTemplate ?? loaded.Settings.OutputTemplate,
      CollisionPolicy = options.Overwrite ? CollisionPolicy.Overwrite : CollisionPolicy.AvoidCollision,
    };
    return new(settings, profile, []);
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
