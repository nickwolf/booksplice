using System.Text;
using BookSplice.Core.Metadata;
using BookSplice.Core.Planning;
using BookSplice.Core.Presets;
using BookSplice.Core.Settings;

namespace BookSplice.Core.Tests.Presets;

public sealed class PresetStoreTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(), "bs-presets-" + Guid.NewGuid().ToString("N"));
  private string FilePath => PresetStore.DefaultPath(_root);

  public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

  private static ConversionPreset Sample(string name = "Commute") => new(name, "efficient", ChannelPolicy.ForceMono, false, ValidationLevel.Full, "GenericMp4", "{author}\\{title}");

  private PresetStore Store() => new(FilePath);

  [Fact]
  public void Missing_file_loads_as_empty_and_can_save()
  {
    var result = Store().Load();
    Assert.Equal(PresetLoadCode.Missing, result.Code);
    Assert.Empty(result.Presets);
    Assert.True(result.CanSave);
  }

  [Fact]
  public void Saved_presets_round_trip_with_schema_version()
  {
    var store = Store();
    var withFolder = Sample("Archive") with { OutputDirectory = Path.Combine(_root, "out"), OutputTemplate = null };
    store.Save([Sample(), withFolder]);

    var loaded = store.Load();

    Assert.Equal(PresetLoadCode.Valid, loaded.Code);
    Assert.Equal([Sample(), withFolder], loaded.Presets);
    Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(FilePath));
    Assert.DoesNotContain("outputTemplate", File.ReadAllText(FilePath).Split("\"name\": \"Archive\"")[1]);
  }

  [Fact]
  public void Save_leaves_no_temp_files_and_replaces_the_old_file()
  {
    var store = Store();
    store.Save([Sample("One")]);
    store.Save([Sample("Two")]);

    Assert.Equal(["Two"], store.Load().Presets.Select(preset => preset.Name));
    Assert.Equal([FilePath], Directory.GetFiles(Path.GetDirectoryName(FilePath)!));
  }

  [Fact]
  public void Failed_replace_keeps_the_old_file_and_removes_the_temp_file()
  {
    var store = Store();
    store.Save([Sample("Keep")]);
    var before = File.ReadAllBytes(FilePath);

    using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
      Assert.ThrowsAny<IOException>(() => store.Save([Sample("Lost")]));

    Assert.Equal(before, File.ReadAllBytes(FilePath));
    Assert.Equal([FilePath], Directory.GetFiles(Path.GetDirectoryName(FilePath)!));
  }

  [Fact]
  public void Names_are_unique_ignoring_case_and_trimmed()
  {
    var store = Store();
    Assert.Throws<ArgumentException>(() => store.Save([Sample("Commute"), Sample("COMMUTE")]));
    Assert.Throws<ArgumentException>(() => store.Save([Sample(" padded ")]));
    Assert.Throws<ArgumentException>(() => store.Save([Sample("   ")]));
    Assert.Throws<ArgumentException>(() => store.Save([Sample(new string('x', PresetNames.MaximumLength + 1))]));
    Assert.False(File.Exists(FilePath));
  }

  [Fact]
  public void Find_ignores_case_and_surrounding_spaces()
  {
    var store = Store();
    store.Save([Sample("Commute")]);
    Assert.NotNull(store.Load().Find("  commute "));
    Assert.Null(store.Load().Find("other"));
  }

  [Fact]
  public void Corrupt_file_is_reported_and_never_overwritten()
  {
    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
    File.WriteAllText(FilePath, "{ not json");
    var store = Store();

    var loaded = store.Load();
    var exception = Assert.Throws<PresetStoreException>(() => store.Save([Sample()]));

    Assert.Equal(PresetLoadCode.Corrupt, loaded.Code);
    Assert.False(loaded.CanSave);
    Assert.Empty(loaded.Presets);
    Assert.Contains("left unchanged", exception.Message);
    Assert.Equal("{ not json", File.ReadAllText(FilePath));
  }

  [Fact]
  public void Future_schema_is_refused_and_preserved()
  {
    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
    const string text = "{\"schemaVersion\":2,\"presets\":[]}";
    File.WriteAllText(FilePath, text);
    var store = Store();

    Assert.Equal(PresetLoadCode.UnsupportedFutureSchema, store.Load().Code);
    Assert.Throws<PresetStoreException>(() => store.Save([Sample()]));
    Assert.Equal(text, File.ReadAllText(FilePath));
  }

  [Fact]
  public void Entry_with_a_bad_value_names_the_preset_and_field_and_blocks_saving()
  {
    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
    var text = """
      {"schemaVersion":1,"presets":[
        {"name":"Good","qualityProfileId":"efficient","channelPolicy":"ForceMono","createChapters":true,"validationLevel":"Full","metadataProfileId":"GenericMp4"},
        {"name":"Broken","qualityProfileId":"efficient","channelPolicy":"7","createChapters":"yes","validationLevel":"Full","metadataProfileId":"GenericMp4"}]}
      """;
    File.WriteAllText(FilePath, text);
    var store = Store();

    var loaded = store.Load();

    Assert.Equal(PresetLoadCode.HasInvalidEntries, loaded.Code);
    Assert.Equal(["Good"], loaded.Presets.Select(preset => preset.Name));
    Assert.Contains(loaded.Errors, error => error.Contains("Preset 'Broken': channelPolicy '7'"));
    Assert.Contains(loaded.Errors, error => error.Contains("Preset 'Broken': createChapters"));
    Assert.Throws<PresetStoreException>(() => store.Save(loaded.Presets));
    Assert.Equal(text, File.ReadAllText(FilePath));
  }

  [Fact]
  public void Duplicate_names_in_the_file_are_reported()
  {
    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
    var one = "{\"name\":\"A\",\"qualityProfileId\":\"efficient\",\"channelPolicy\":\"ForceMono\",\"createChapters\":true,\"validationLevel\":\"Full\",\"metadataProfileId\":\"GenericMp4\"}";
    File.WriteAllText(FilePath, $"{{\"schemaVersion\":1,\"presets\":[{one},{one.Replace("\"A\"", "\"a\"")}]}}");

    var loaded = Store().Load();

    Assert.Equal(PresetLoadCode.HasInvalidEntries, loaded.Code);
    Assert.Contains(loaded.Errors, error => error.Contains("appears more than once"));
  }

  [Fact]
  public void Validator_names_the_preset_and_each_bad_field()
  {
    var bad = new ConversionPreset("Odd", "no-such-quality", ChannelPolicy.ForceMono, true, ValidationLevel.Full, "Missing", "{nope}", "relative");

    var errors = PresetValidator.Validate(bad, MetadataProfileCatalog.BuiltIn);

    Assert.Equal(4, errors.Count);
    Assert.All(errors, error => Assert.StartsWith("Preset 'Odd': ", error));
    Assert.Contains(errors, error => error.Contains("qualityProfileId 'no-such-quality'"));
    Assert.Contains(errors, error => error.Contains("metadataProfileId 'Missing'") && error.Contains("GenericMp4"));
    Assert.Contains(errors, error => error.Contains("outputTemplate: ") && error.Contains("{nope}"));
    Assert.Contains(errors, error => error.Contains("outputDirectory"));
  }

  [Fact]
  public void Apply_copies_values_and_leaves_absent_optional_fields_alone()
  {
    var job = AppSettings.Defaults with { OutputDirectory = Path.Combine(_root, "job"), OutputTemplate = "{series}\\{title}" };
    var preset = Sample() with { OutputTemplate = null };

    var result = PresetApplier.Apply(job, preset, MetadataProfileCatalog.BuiltIn);

    Assert.True(result.IsSuccess);
    Assert.Equal(job with { QualityProfileId = "efficient", ChannelPolicy = ChannelPolicy.ForceMono, CreateChapters = false, ValidationLevel = ValidationLevel.Full }, result.Settings);
  }

  [Fact]
  public void Apply_refuses_an_invalid_preset_without_falling_back()
  {
    var result = PresetApplier.Apply(AppSettings.Defaults, Sample() with { QualityProfileId = "gone" }, MetadataProfileCatalog.BuiltIn);

    Assert.False(result.IsSuccess);
    Assert.Null(result.Settings);
    Assert.Contains("Preset 'Commute': qualityProfileId 'gone'", Assert.Single(result.Errors));
  }

  [Fact]
  public void Export_omits_the_output_folder_unless_asked()
  {
    var preset = Sample() with { OutputDirectory = Path.Combine(_root, "private-output") };

    var plain = PresetPortable.Export([preset], includeOutputDirectory: false);
    var withFolder = PresetPortable.Export([preset], includeOutputDirectory: true);

    Assert.DoesNotContain("outputDirectory", plain);
    Assert.DoesNotContain("private-output", plain);
    Assert.Contains("\"outputDirectory\"", withFolder);
    Assert.Contains("private-output", withFolder);
    Assert.Contains("\"schemaVersion\": 1", plain);
  }

  [Fact]
  public void Import_reports_each_preset_and_never_overwrites_a_clash()
  {
    var text = PresetPortable.Export([Sample("Fresh"), Sample("commute") with { QualityProfileId = "preserve-more" }, Sample("Bad") with { MetadataProfileId = "Nope" }], false);

    var report = PresetPortable.Import(Encoding.UTF8.GetBytes(text), [Sample("Commute")], MetadataProfileCatalog.BuiltIn);

    Assert.Equal(["Fresh"], report.Accepted.Select(preset => preset.Name));
    Assert.Equal([PresetImportStatus.Imported, PresetImportStatus.NameClash, PresetImportStatus.Rejected], report.Items.Select(item => item.Status));
    var lines = report.Describe();
    Assert.Contains(lines, line => line.Contains("'commute'") && line.Contains("already exists"));
    Assert.Contains(lines, line => line.Contains("'Bad'") && line.Contains("metadataProfileId 'Nope'"));
  }

  [Fact]
  public void Import_refuses_a_future_schema_file()
  {
    var report = PresetPortable.Import(Encoding.UTF8.GetBytes("{\"schemaVersion\":9,\"presets\":[]}"), [], MetadataProfileCatalog.BuiltIn);

    Assert.Equal(PresetLoadCode.UnsupportedFutureSchema, report.Code);
    Assert.False(report.IsFileUsable);
    Assert.Empty(report.Accepted);
    Assert.NotEmpty(report.Describe());
  }
}
