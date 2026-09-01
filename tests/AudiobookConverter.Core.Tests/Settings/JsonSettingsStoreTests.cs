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

  [Fact]
  public async Task Invalid_json_never_exposes_output_path()
  {
    var root = Path.Combine(Path.GetTempPath(), "abc-settings-" + Guid.NewGuid()); var store = new JsonSettingsStore(root); Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
    await File.WriteAllTextAsync(store.SettingsPath, "{\"schemaVersion\":1,\"outputDirectory\":\"C:\\\\secret"); var result = await store.LoadAsync();
    Assert.Null(result.Settings); Assert.False(result.IsUsable);
  }

  [Fact]
  public async Task Unknown_nested_property_is_preserved_on_save()
  {
    var root = Path.Combine(Path.GetTempPath(), "abc-settings-" + Guid.NewGuid()); var store = new JsonSettingsStore(root); Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
    var json = "{\"schemaVersion\":1,\"outputDirectory\":\"" + root.Replace("\\", "\\\\") + "\",\"qualityProfileId\":\"high-quality\",\"channelPolicy\":\"PreserveSourceChannels\",\"createChapters\":true,\"conversionJobs\":null,\"collisionPolicy\":\"AvoidCollision\",\"metadataProfileId\":\"GenericMp4\",\"validationLevel\":\"Lightweight\",\"logLevel\":\"Information\",\"future\":{\"nested\":[1,true]}}";
    await File.WriteAllTextAsync(store.SettingsPath, json); var result = await store.LoadAsync(); await store.SaveAsync(result.Settings!); var saved = await File.ReadAllTextAsync(store.SettingsPath);
    Assert.Contains("\"future\"", saved); Assert.Contains("\"nested\"", saved); Assert.Contains("\"qualityProfileId\": \"high-quality\"", saved);
  }

  [Fact]
  public async Task Schema_zero_outputPath_is_migrated()
  {
    var root = Path.Combine(Path.GetTempPath(), "abc-settings-" + Guid.NewGuid()); var store = new JsonSettingsStore(root); Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
    var json = "{\"schemaVersion\":0,\"outputPath\":\"" + root.Replace("\\", "\\\\") + "\",\"qualityProfileId\":\"high-quality\",\"channelPolicy\":\"PreserveSourceChannels\",\"createChapters\":true,\"conversionJobs\":null,\"collisionPolicy\":\"AvoidCollision\",\"metadataProfileId\":\"GenericMp4\",\"validationLevel\":\"Lightweight\",\"logLevel\":\"Information\"}";
    await File.WriteAllTextAsync(store.SettingsPath, json); var result = await store.LoadAsync(); Assert.Equal(SettingsLoadCode.Migrated, result.Code); Assert.Equal(1, result.Settings?.SchemaVersion); Assert.Equal(root, result.Settings?.OutputDirectory);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(33)]
  public async Task Out_of_range_conversion_jobs_are_rejected(int jobs)
  {
    var root = Path.Combine(Path.GetTempPath(), "abc-settings-" + Guid.NewGuid()); var store = new JsonSettingsStore(root); Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
    var json = "{\"schemaVersion\":1,\"outputDirectory\":\"" + root.Replace("\\", "\\\\") + "\",\"qualityProfileId\":\"high-quality\",\"channelPolicy\":\"PreserveSourceChannels\",\"createChapters\":true,\"conversionJobs\":" + jobs + ",\"collisionPolicy\":\"AvoidCollision\",\"metadataProfileId\":\"GenericMp4\",\"validationLevel\":\"Lightweight\",\"logLevel\":\"Information\"}";
    await File.WriteAllTextAsync(store.SettingsPath, json); var result = await store.LoadAsync(); Assert.Equal(SettingsLoadCode.ValidationFailed, result.Code); Assert.Null(result.Settings);
  }

  private static async Task<byte[]> ReadPreambleAsync(string path)
  {
    await using var stream = File.OpenRead(path);
    var bytes = new byte[3];
    _ = await stream.ReadAsync(bytes);
    return bytes;
  }
}
