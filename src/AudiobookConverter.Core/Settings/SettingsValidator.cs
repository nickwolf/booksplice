using AudiobookConverter.Core.Metadata;
using AudiobookConverter.Core.Naming;
using AudiobookConverter.Core.Planning;

namespace AudiobookConverter.Core.Settings;

public sealed record SettingsValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
  public static SettingsValidationResult Valid { get; } = new(true, []);
}

public static class SettingsValidator
{
  public static SettingsValidationResult Validate(AppSettings? settings)
  {
    if (settings is null) return new(false, ["settings is null"]);
    var errors = new List<string>();
    if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion) errors.Add("unsupported schema version");
    if (string.IsNullOrWhiteSpace(settings.OutputDirectory) || !Path.IsPathFullyQualified(settings.OutputDirectory) || settings.OutputDirectory.Contains('\0')) errors.Add("output directory must be a fully qualified path without NUL");
    if (QualityProfileCatalog.FindById(settings.QualityProfileId) is null) errors.Add("unknown quality profile");
    if (!Enum.IsDefined(settings.ChannelPolicy)) errors.Add("undefined channel policy");
    if (!Enum.IsDefined(settings.CollisionPolicy)) errors.Add("undefined collision policy");
    if (!Enum.IsDefined(settings.ValidationLevel)) errors.Add("undefined validation level");
    if (!Enum.IsDefined(settings.LogLevel)) errors.Add("undefined log level");
    if (settings.ConversionJobs is < 1 or > 32) errors.Add("conversion jobs must be null or between 1 and 32");
    if (!string.Equals(settings.MetadataProfileId, MetadataProfiles.GenericMp4.Name, StringComparison.Ordinal) && !string.Equals(settings.MetadataProfileId, MetadataProfiles.NickMp3tag.Name, StringComparison.Ordinal)) errors.Add("unknown metadata profile");
    return errors.Count == 0 ? SettingsValidationResult.Valid : new(false, errors);
  }
}
