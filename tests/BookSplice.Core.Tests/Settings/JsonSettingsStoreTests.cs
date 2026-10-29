using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using System.Text;

namespace BookSplice.Core.Tests.Settings;

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
    Directory.CreateDirectory(Path.Combine(root, "BookSplice"));
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

  private static string SettingsJson(string root, string jobs, string? booksAtOnce)
    => "{\"schemaVersion\":1,\"outputDirectory\":\"" + root.Replace("\\", "\\\\") + "\",\"qualityProfileId\":\"high-quality\",\"channelPolicy\":\"PreserveSourceChannels\",\"createChapters\":true,\"conversionJobs\":" + jobs + (booksAtOnce is null ? "" : ",\"booksAtOnce\":" + booksAtOnce) + ",\"collisionPolicy\":\"AvoidCollision\",\"metadataProfileId\":\"GenericMp4\",\"validationLevel\":\"Lightweight\",\"logLevel\":\"Information\"}";

  [Theory]
  [InlineData("4", null, 4, 4)]
  [InlineData("null", null, null, null)]
  [InlineData("4", "2", 4, 2)]
  [InlineData("4", "null", 4, null)]
  public async Task Books_at_once_migrates_from_conversion_jobs_only_when_absent(string jobs, string? booksAtOnce, int? expectedJobs, int? expectedBooks)
  {
    var root = Path.Combine(Path.GetTempPath(), "abc-settings-" + Guid.NewGuid()); var store = new JsonSettingsStore(root); Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
    await File.WriteAllTextAsync(store.SettingsPath, SettingsJson(root, jobs, booksAtOnce));
    var settings = (await store.LoadAsync()).Settings!;
    Assert.Equal(expectedJobs, settings.ConversionJobs); Assert.Equal(expectedBooks, settings.BooksAtOnce);
    var snapshot = JsonSettingsStore.ParseSnapshot(System.Text.Encoding.UTF8.GetBytes(SettingsJson(root, jobs, booksAtOnce)));
    Assert.Equal(SettingsLoadCode.Valid, snapshot.Code); Assert.Equal(expectedBooks, snapshot.Settings!.BooksAtOnce);
  }

  [Fact]
  public async Task Books_at_once_round_trips_and_file_keeps_schema_one()
  {
    var root = Path.Combine(Path.GetTempPath(), "abc-settings-" + Guid.NewGuid()); var store = new JsonSettingsStore(root);
    await store.SaveAsync(AppSettings.Defaults with { OutputDirectory = root, ConversionJobs = 3, BooksAtOnce = 2 });
    var bytes = await File.ReadAllBytesAsync(store.SettingsPath);
    var snapshot = JsonSettingsStore.ParseSnapshot(bytes);
    Assert.Equal(SettingsLoadCode.Valid, snapshot.Code); Assert.Equal(1, snapshot.Settings!.SchemaVersion);
    Assert.Equal(3, snapshot.Settings.ConversionJobs); Assert.Equal(2, snapshot.Settings.BooksAtOnce);
  }

  [Fact]
  public async Task Output_template_defaults_when_absent_round_trips_and_keeps_schema_one()
  {
    var root = Path.Combine(Path.GetTempPath(), "abc-settings-" + Guid.NewGuid()); var store = new JsonSettingsStore(root); Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
    await File.WriteAllTextAsync(store.SettingsPath, SettingsJson(root, "null", null));
    var older = (await store.LoadAsync()).Settings!;
    Assert.Equal("{title}", older.OutputTemplate); Assert.Equal("{title}", AppSettings.Defaults.OutputTemplate);
    await store.SaveAsync(older with { OutputTemplate = @"{author}\{series}\{title}" });
    var snapshot = JsonSettingsStore.ParseSnapshot(await File.ReadAllBytesAsync(store.SettingsPath));
    Assert.Equal(SettingsLoadCode.Valid, snapshot.Code); Assert.Equal(1, snapshot.Settings!.SchemaVersion); Assert.Equal(@"{author}\{series}\{title}", snapshot.Settings.OutputTemplate);
    Assert.Contains("\"outputTemplate\"", await File.ReadAllTextAsync(store.SettingsPath));
  }

  [Theory]
  [InlineData("{nope}")]
  [InlineData("..\\{title}")]
  [InlineData("")]
  public void Invalid_output_template_fails_validation(string template)
    => Assert.False(SettingsValidator.Validate(AppSettings.Defaults with { OutputDirectory = Path.GetTempPath(), OutputTemplate = template }).IsValid);

  [Fact]
  public async Task Missing_file_defaults_to_one_book_at_a_time_when_unset()
  {
    Assert.Null(AppSettings.Defaults.BooksAtOnce);
    var root = Path.Combine(Path.GetTempPath(), "abc-settings-" + Guid.NewGuid()); var store = new JsonSettingsStore(root);
    Assert.Null((await store.LoadAsync()).Settings!.BooksAtOnce);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(33)]
  public async Task Out_of_range_books_at_once_is_rejected(int books)
  {
    var root = Path.Combine(Path.GetTempPath(), "abc-settings-" + Guid.NewGuid()); var store = new JsonSettingsStore(root); Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
    await File.WriteAllTextAsync(store.SettingsPath, SettingsJson(root, "null", books.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    var result = await store.LoadAsync(); Assert.Equal(SettingsLoadCode.ValidationFailed, result.Code); Assert.Null(result.Settings);
  }

  private static async Task<byte[]> ReadPreambleAsync(string path)
  {
    await using var stream = File.OpenRead(path);
    var bytes = new byte[3];
    _ = await stream.ReadAsync(bytes);
    return bytes;
  }
}
