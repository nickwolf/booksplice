using AudiobookConverter.Core.Naming;
using AudiobookConverter.Core.Planning;
using AudiobookConverter.Core.Settings;
using System.Text;

namespace AudiobookConverter.Core.Tests.Settings;

public sealed class JsonSettingsStoreTests
{
  [Fact]
  public async Task Missing_file_returns_safe_defaults_without_creating_directory()
  {
    var root = Path.Combine(Path.GetTempPath(), "abc-settings-" + Guid.NewGuid());
    var store = new JsonSettingsStore(root);

    var result = await store.LoadAsync();

    Assert.Equal(SettingsLoadCode.Missing, result.Code);
    Assert.False(result.IsUsable);
    Assert.Equal(string.Empty, result.Settings?.OutputDirectory);
    Assert.False(Directory.Exists(Path.GetDirectoryName(store.SettingsPath)!));
  }

  [Fact]
  public void Defaults_use_public_safe_values()
  {
    var settings = AppSettings.Defaults;

    Assert.Equal(1, settings.SchemaVersion);
    Assert.Equal("high-quality", settings.QualityProfileId);
    Assert.Equal(ChannelPolicy.PreserveSourceChannels, settings.ChannelPolicy);
    Assert.True(settings.CreateChapters);
    Assert.Null(settings.ConversionJobs);
    Assert.Equal(CollisionPolicy.AvoidCollision, settings.CollisionPolicy);
    Assert.Equal("GenericMp4", settings.MetadataProfileId);
    Assert.Equal(ValidationLevel.Lightweight, settings.ValidationLevel);
    Assert.Equal(LogLevel.Information, settings.LogLevel);
    Assert.Equal(string.Empty, settings.OutputDirectory);
  }

  [Fact]
  public async Task Valid_settings_round_trip_as_camel_case_utf8_json()
  {
    var root = Path.Combine(Path.GetTempPath(), "abc-settings-" + Guid.NewGuid());
    Directory.CreateDirectory(Path.Combine(root, "AudiobookConverter"));
    var settings = AppSettings.Defaults with { OutputDirectory = Path.GetFullPath(root), CreateChapters = false };
    var store = new JsonSettingsStore(root);

    await store.SaveAsync(settings);
    var loaded = await store.LoadAsync();
    var json = await File.ReadAllTextAsync(store.SettingsPath, Encoding.UTF8);

    Assert.True(loaded.IsUsable);
    Assert.Equal(settings.OutputDirectory, loaded.Settings?.OutputDirectory);
    Assert.Equal(settings.CreateChapters, loaded.Settings?.CreateChapters);
    Assert.Contains("outputDirectory", json);
    Assert.DoesNotContain("OutputDirectory", json);
    Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, await ReadPreambleAsync(store.SettingsPath));
  }

  private static async Task<byte[]> ReadPreambleAsync(string path)
  {
    await using var stream = File.OpenRead(path);
    var bytes = new byte[3];
    _ = await stream.ReadAsync(bytes);
    return bytes;
  }
}
