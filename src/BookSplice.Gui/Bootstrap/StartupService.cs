using BookSplice.Core.Settings;

namespace BookSplice.Gui.Bootstrap;

public sealed record StartupResult(AppSettings? Settings, bool RequiresSetup, bool IsBlocked, string Message);

public sealed class StartupService(ISettingsStore store, IOutputDirectoryProbe? probe = null)
{
  public async Task<StartupResult> LoadAsync(CancellationToken cancellationToken = default)
  {
    var loaded = await store.LoadAsync(cancellationToken);
    if (loaded.Code == SettingsLoadCode.Missing) return new(AppSettings.Defaults, true, false, "");
    if (!loaded.IsUsable)
      return new(null, false, true, $"BookSplice could not use the saved settings ({loaded.Code}). Your file has not been changed. Back it up and repair it, or move it aside to run setup again. Settings: {store.SettingsPath}");
    var destination = await (probe ?? new FileOutputDirectoryProbe()).ProbeAsync(loaded.Settings!.OutputDirectory, cancellationToken);
    return new(loaded.Settings, !destination.Succeeded, false,
      destination.Succeeded ? "" : "The saved output folder is unavailable or cannot be written. Choose a writable folder.");
  }
}
