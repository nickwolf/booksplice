using BookSplice.Core.Metadata;
using BookSplice.Core.Settings;
using BookSplice.Gui.Bootstrap;
using BookSplice.Gui.Diagnostics;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class MetadataProfileGuiTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(), "BookSplice-tests", Guid.NewGuid().ToString("N"));
  private readonly string _profiles;

  public MetadataProfileGuiTests() => _profiles = Directory.CreateDirectory(Path.Combine(_root, "profiles")).FullName;

  [Fact]
  public async Task MissingSavedProfileRequiresSetupWithLoadedSettingsAndLeavesTheFileUntouched()
  {
    var catalog = Catalog();
    var output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
    var store = new JsonSettingsStore(_root);
    await store.SaveAsync(AppSettings.Defaults with { OutputDirectory = output, QualityProfileId = "efficient", MetadataProfileId = "SavedButGone" });
    var before = await File.ReadAllBytesAsync(store.SettingsPath);

    var startup = await new StartupService(store, profiles: catalog).LoadAsync();

    Assert.True(startup.RequiresSetup);
    Assert.False(startup.IsBlocked);
    Assert.Equal("efficient", startup.Settings!.QualityProfileId);
    Assert.Equal("SavedButGone", startup.Settings.MetadataProfileId);
    Assert.Contains("SavedButGone", startup.Message);
    Assert.Contains("has not been changed", startup.Message);
    Assert.DoesNotContain("skipped", startup.Message);
    Assert.Equal(before, await File.ReadAllBytesAsync(store.SettingsPath));
  }

  [Fact]
  public async Task MissingSavedProfileMessageIncludesTheRejectedFileReason()
  {
    var output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
    var store = new JsonSettingsStore(_root);
    await store.SaveAsync(AppSettings.Defaults with { OutputDirectory = output, MetadataProfileId = "BrokenProfile" });

    var startup = await new StartupService(store, profiles: Catalog()).LoadAsync();

    Assert.Contains("broken.json was rejected", startup.Message);
  }

  [Fact]
  public async Task ProfilePresentInTheCatalogDoesNotRequireSetup()
  {
    var output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
    var store = new JsonSettingsStore(_root);
    await store.SaveAsync(AppSettings.Defaults with { OutputDirectory = output, MetadataProfileId = "SyntheticCustom" });

    var startup = await new StartupService(store, profiles: Catalog()).LoadAsync();

    Assert.False(startup.RequiresSetup);
  }

  [Fact]
  public void FirstLaunchListsCustomProfilesAndFallsBackToGenericForAMissingSavedOne()
  {
    var catalog = Catalog();
    var service = new FirstLaunchService(new JsonSettingsStore(_root), profiles: catalog);

    var missing = new FirstLaunchViewModel(service, AppSettings.Defaults with { MetadataProfileId = "SavedButGone" }, catalog);
    var saved = new FirstLaunchViewModel(service, AppSettings.Defaults with { MetadataProfileId = "SyntheticCustom" }, catalog);

    Assert.Equal(["GenericMp4", "SyntheticCustom"], missing.MetadataProfiles);
    Assert.Equal("GenericMp4", missing.MetadataProfileId);
    Assert.Equal("SyntheticCustom", saved.MetadataProfileId);
  }

  [Fact]
  public async Task FirstLaunchServiceRejectsAProfileOutsideTheCatalog()
  {
    var store = new JsonSettingsStore(_root);
    var settings = AppSettings.Defaults with { OutputDirectory = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName, MetadataProfileId = "SavedButGone" };

    var result = await new FirstLaunchService(store, profiles: Catalog()).CompleteAsync(settings);

    Assert.False(result.Completed);
    Assert.Equal("profile-unavailable", result.Code);
    Assert.False(File.Exists(store.SettingsPath));
  }

  [Fact]
  public void FirstLaunchShowsASkippedFilesNoticeOnlyWhenFilesWereRejected()
  {
    var catalog = Catalog();
    var service = new FirstLaunchService(new JsonSettingsStore(_root), profiles: catalog);

    var notice = new FirstLaunchViewModel(service, AppSettings.Defaults, catalog).ProfileNotice;

    Assert.Contains("1 metadata profile file(s) were skipped", notice);
    Assert.Contains("broken.json", notice);
    Assert.Equal("", new FirstLaunchViewModel(service, AppSettings.Defaults).ProfileNotice);
  }

  [Fact]
  public void QueueItemAcceptsACatalogIdAndIgnoresOthers()
  {
    var item = new BookQueueItemViewModel("synthetic-book", AppSettings.Defaults, Catalog());

    item.MetadataProfileId = "SyntheticCustom";
    Assert.Equal("SyntheticCustom", item.Settings.MetadataProfileId);
    item.MetadataProfileId = "Elsewhere";
    Assert.Equal("SyntheticCustom", item.Settings.MetadataProfileId);
    Assert.Contains("SyntheticCustom", item.MetadataProfiles);
  }

  [Fact]
  public void PublicDiagnosticsNameOnlyBuiltInProfiles()
  {
    var catalog = Catalog();
    var custom = new BookQueueItemViewModel("synthetic-book", AppSettings.Defaults with { MetadataProfileId = "SyntheticCustom" }, catalog);
    var builtIn = new BookQueueItemViewModel("synthetic-book", AppSettings.Defaults, catalog);

    var customText = DiagnosticsTextBuilder.Build(custom);

    Assert.Contains("Metadata profile: Custom", customText);
    Assert.DoesNotContain("SyntheticCustom", customText);
    Assert.Contains("Metadata profile: GenericMp4", DiagnosticsTextBuilder.Build(builtIn));
  }

  private MetadataProfileCatalog Catalog()
  {
    File.WriteAllText(Path.Combine(_profiles, "custom.json"), """{"schemaVersion":1,"name":"SyntheticCustom","version":"1","mappings":[{"field":"BookTitle","keys":["TITLE"]}]}""");
    File.WriteAllText(Path.Combine(_profiles, "broken.json"), """{"schemaVersion":1,"name":"BrokenProfile","version":"1","mappings":[]}""");
    return MetadataProfileCatalog.Load(_profiles);
  }

  public void Dispose()
  {
    if (Directory.Exists(_root)) Directory.Delete(_root, true);
  }
}
