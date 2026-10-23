using BookSplice.Core.Metadata;
using BookSplice.Core.Settings;

namespace BookSplice.Gui.Bootstrap;

public sealed record StartupResult(AppSettings? Settings, bool RequiresSetup, bool IsBlocked, string Message);

public sealed class StartupService(ISettingsStore store, IOutputDirectoryProbe? probe = null, MetadataProfileCatalog? profiles = null)
{
  public async Task<StartupResult> LoadAsync(CancellationToken cancellationToken = default)
  {
    var loaded = await store.LoadAsync(cancellationToken);
    if (loaded.Code == SettingsLoadCode.Missing) return new(AppSettings.Defaults, true, false, "");
    if (!loaded.IsUsable)
      return new(null, false, true, $"BookSplice could not use the saved settings ({loaded.Code}). Your file has not been changed. Back it up and repair it, or move it aside to run setup again. Settings: {store.SettingsPath}");
    var destination = await (probe ?? new FileOutputDirectoryProbe()).ProbeAsync(loaded.Settings!.OutputDirectory, cancellationToken);
    var catalog = profiles ?? MetadataProfileCatalog.BuiltIn;
    var profileId = loaded.Settings.MetadataProfileId;
    var profileMissing = catalog.Find(profileId) is null;
    if (destination.Succeeded && !profileMissing) return new(loaded.Settings, false, false, "");
    var parts = new List<string>();
    if (profileMissing)
    {
      var rejected = catalog.Errors.FirstOrDefault(error => string.Equals(error.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
      parts.Add($"The saved metadata profile '{profileId}' is not available" + (rejected is null ? "." : $" ({rejected.FileName} was rejected: {rejected.Reason})") + " Your settings file has not been changed. Choose another metadata profile.");
    }
    if (!destination.Succeeded) parts.Add("The saved output folder is unavailable or cannot be written. Choose a writable folder.");
    return new(loaded.Settings, true, false, string.Join(" ", parts));
  }
}
