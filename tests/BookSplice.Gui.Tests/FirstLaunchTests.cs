using BookSplice.Core.Settings;
using BookSplice.Gui.Bootstrap;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class FirstLaunchTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(), "BookSplice-tests", Guid.NewGuid().ToString("N"));

  [Fact]
  public async Task FreshProfileRequiresSetupWithoutWritingSettings()
  {
    var store = new JsonSettingsStore(_root);
    var startup = await new StartupService(store).LoadAsync();
    Assert.True(startup.RequiresSetup);
    Assert.False(startup.IsBlocked);
    Assert.False(File.Exists(store.SettingsPath));
  }

  [Fact]
  public async Task SavePersistsChoicesAndNextStartupSkipsSetup()
  {
    Directory.CreateDirectory(_root);
    var store = new JsonSettingsStore(_root);
    var model = new FirstLaunchViewModel(new FirstLaunchService(store), AppSettings.Defaults)
    { OutputDirectory = _root, QualityProfileId = "balanced", CreateChapters = false };
    Assert.True(await model.SaveAsync());
    Assert.True(model.Completed);
    var startup = await new StartupService(store).LoadAsync();
    Assert.False(startup.RequiresSetup);
    Assert.False(startup.IsBlocked);
    Assert.Equal("balanced", startup.Settings!.QualityProfileId);
    Assert.False(startup.Settings.CreateChapters);
    Assert.Empty(Directory.GetFiles(_root, ".booksplice-probe-*"));
  }

  [Fact]
  public async Task MissingDestinationDoesNotCompleteOrSave()
  {
    var store = new JsonSettingsStore(_root);
    var model = new FirstLaunchViewModel(new FirstLaunchService(store), AppSettings.Defaults)
    { OutputDirectory = Path.Combine(_root, "missing") };
    Assert.False(await model.SaveAsync());
    Assert.False(model.Completed);
    Assert.False(model.IsBusy);
    Assert.NotEmpty(model.ErrorMessage);
    Assert.False(File.Exists(store.SettingsPath));
  }

  [Theory]
  [InlineData("not json")]
  [InlineData("{\"schemaVersion\":999}")]
  public async Task UnreadableSettingsBlockStartupAndRemainUnchanged(string contents)
  {
    var store = new JsonSettingsStore(_root);
    Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
    await File.WriteAllTextAsync(store.SettingsPath, contents);
    var startup = await new StartupService(store).LoadAsync();
    Assert.True(startup.IsBlocked);
    Assert.False(startup.RequiresSetup);
    Assert.Equal(contents, await File.ReadAllTextAsync(store.SettingsPath));
  }

  [Fact]
  public async Task UnavailableSavedDestinationRequiresSetupWithExistingChoices()
  {
    var store = new JsonSettingsStore(_root);
    await store.SaveAsync(AppSettings.Defaults with { OutputDirectory = Path.Combine(_root, "missing"), QualityProfileId = "efficient" });
    var startup = await new StartupService(store).LoadAsync();
    Assert.True(startup.RequiresSetup);
    Assert.Equal("efficient", startup.Settings!.QualityProfileId);
  }

  [Fact]
  public async Task CancelledSaveDoesNotCompleteOrWrite()
  {
    Directory.CreateDirectory(_root);
    var store = new JsonSettingsStore(_root);
    var model = new FirstLaunchViewModel(new FirstLaunchService(store), AppSettings.Defaults) { OutputDirectory = _root };
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    Assert.False(await model.SaveAsync(cancellation.Token));
    Assert.False(model.Completed);
    Assert.False(model.IsBusy);
    Assert.False(File.Exists(store.SettingsPath));
  }

  [Fact]
  public async Task SaveFailureKeepsSetupOpenAndReportsError()
  {
    Directory.CreateDirectory(_root);
    var model = new FirstLaunchViewModel(new FirstLaunchService(new FailingStore()), AppSettings.Defaults) { OutputDirectory = _root };
    Assert.False(await model.SaveAsync());
    Assert.False(model.Completed);
    Assert.False(model.IsBusy);
    Assert.Contains("disk full", model.ErrorMessage);
  }

  [Fact]
  public async Task CompletedSetupCannotBeSubmittedAgain()
  {
    Directory.CreateDirectory(_root);
    var store = new JsonSettingsStore(_root);
    var model = new FirstLaunchViewModel(new FirstLaunchService(store), AppSettings.Defaults) { OutputDirectory = _root };
    Assert.True(await model.SaveAsync());
    model.QualityProfileId = "efficient";
    Assert.False(await model.SaveAsync());
    Assert.Equal("high-quality", (await store.LoadAsync()).Settings!.QualityProfileId);
  }

  private sealed class FailingStore : ISettingsStore
  {
    public string SettingsPath => "settings.json";
    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => throw new IOException("disk full");
  }

  [Fact]
  public async Task AdvancedSettingsPersistThroughSetup()
  {
    Directory.CreateDirectory(_root);
    var store = new JsonSettingsStore(_root);
    var model = new FirstLaunchViewModel(new FirstLaunchService(store), AppSettings.Defaults)
    {
      OutputDirectory = _root,
      ConversionJobs = 3,
      MetadataProfileId = "NickMp3tag",
      ValidationLevel = ValidationLevel.Full,
      LogLevel = LogLevel.Debug
    };
    Assert.True(await model.SaveAsync());
    var saved = (await store.LoadAsync()).Settings!;
    Assert.Equal(3, saved.ConversionJobs);
    Assert.Equal("NickMp3tag", saved.MetadataProfileId);
    Assert.Equal(ValidationLevel.Full, saved.ValidationLevel);
    Assert.Equal(LogLevel.Debug, saved.LogLevel);
  }

  public void Dispose()
  {
    if (Directory.Exists(_root)) Directory.Delete(_root, true);
  }
}
