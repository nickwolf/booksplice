using AudiobookConverter.Core.Settings;

namespace AudiobookConverter.Core.Tests.Settings;

public sealed class FirstLaunchServiceTests
{
  [Fact]
  public async Task Invalid_settings_do_not_probe_or_save()
  {
    var probe = new RecordingProbe();
    var store = new RecordingStore();
    var service = new FirstLaunchService(store, probe);

    var result = await service.CompleteAsync(AppSettings.Defaults);

    Assert.False(result.Completed);
    Assert.Equal(0, probe.Calls);
    Assert.Equal(0, store.Saves);
  }

  private sealed class RecordingProbe : IOutputDirectoryProbe
  {
    public int Calls { get; private set; }
    public Task<OutputProbeResult> ProbeAsync(string directory, CancellationToken cancellationToken = default)
    {
      Calls++;
      return Task.FromResult(OutputProbeResult.Success());
    }
  }

  private sealed class RecordingStore : ISettingsStore
  {
    public int Saves { get; private set; }
    public string SettingsPath => "settings.json";
    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(SettingsLoadResult.Missing(AppSettings.Defaults));
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { Saves++; return Task.CompletedTask; }
  }
}
