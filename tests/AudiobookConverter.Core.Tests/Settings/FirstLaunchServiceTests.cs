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

  [Fact]
  public async Task Successful_probe_persists_settings()
  {
    var probe = new RecordingProbe(); var store = new RecordingStore(); var settings = AppSettings.Defaults with { OutputDirectory = Path.GetFullPath(Path.GetTempPath()) };
    var result = await new FirstLaunchService(store, probe).CompleteAsync(settings);
    Assert.True(result.Completed); Assert.Equal(1, probe.Calls); Assert.Equal(1, store.Saves);
  }

  [Fact]
  public async Task Probe_failure_does_not_persist()
  {
    var probe = new RecordingProbe { Result = new(false, "cleanup-failed") }; var store = new RecordingStore(); var settings = AppSettings.Defaults with { OutputDirectory = Path.GetFullPath(Path.GetTempPath()) };
    var result = await new FirstLaunchService(store, probe).CompleteAsync(settings);
    Assert.False(result.Completed); Assert.Equal("cleanup-failed", result.Code); Assert.Equal(0, store.Saves);
  }

  [Fact]
  public async Task Missing_destination_does_not_persist()
  {
    var root = Path.Combine(Path.GetTempPath(), "abc-missing-" + Guid.NewGuid()); var store = new RecordingStore(); var settings = AppSettings.Defaults with { OutputDirectory = root };
    var result = await new FirstLaunchService(store).CompleteAsync(settings);
    Assert.False(result.Completed); Assert.Equal(0, store.Saves); Assert.False(Directory.Exists(root));
  }

  [Fact]
  public async Task File_destination_reports_probe_failure_without_persisting()
  {
    var root = Path.Combine(Path.GetTempPath(), "abc-file-" + Guid.NewGuid()); File.WriteAllText(root, "existing");
    var store = new RecordingStore(); var settings = AppSettings.Defaults with { OutputDirectory = root };
    var result = await new FirstLaunchService(store).CompleteAsync(settings);
    Assert.False(result.Completed); Assert.Equal("destination-unavailable", result.Code); Assert.Equal(0, store.Saves); Assert.Equal("existing", File.ReadAllText(root));
  }

  private sealed class RecordingProbe : IOutputDirectoryProbe
  {
    public OutputProbeResult Result { get; set; } = OutputProbeResult.Success();
    public int Calls { get; private set; }
    public Task<OutputProbeResult> ProbeAsync(string directory, CancellationToken cancellationToken = default)
    {
      Calls++;
      return Task.FromResult(Result);
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
