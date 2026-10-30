using System.Text.Json;
using BookSplice.Core.Analysis;
using BookSplice.Core.Execution;
using BookSplice.Core.Planning;
using BookSplice.Core.Presets;
using BookSplice.Core.Settings;

namespace BookSplice.Cli.Tests;

public sealed class PresetCliTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(), $"BookSplice-presets-{Guid.NewGuid():N}");
  private readonly string _output;
  private readonly RecordingService _service = new();

  public PresetCliTests()
  {
    _output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
    Directory.CreateDirectory(Path.Combine(_root, "BookSplice"));
  }

  public void Dispose() => Directory.Delete(_root, recursive: true);

  private PresetStore Presets => new(PresetStore.DefaultPath(_root));

  private async Task<CliApplication> ApplicationAsync(AppSettings? settings = null)
  {
    await new JsonSettingsStore(_root).SaveAsync(settings ?? AppSettings.Defaults with { OutputDirectory = _output, QualityProfileId = "balanced", ConversionJobs = 3 });
    return new CliApplication(new JsonSettingsStore(_root), _service, presets: Presets);
  }

  private static async Task<(CliExitCode Exit, string Output, string Error)> RunAsync(CliApplication application, params string[] arguments)
  {
    var output = new StringWriter();
    var error = new StringWriter();
    var exit = await application.RunAsync(arguments, output, error, CancellationToken.None);
    return (exit, output.ToString(), error.ToString());
  }

  private static ConversionPreset Commute(string name = "Commute") => new(name, "efficient", ChannelPolicy.ForceMono, false, ValidationLevel.Full, "GenericMp4", "{author}\\{title}");

  [Fact]
  public async Task PresetValuesAreAppliedOnTopOfTheSavedDefaults()
  {
    Presets.Save([Commute()]);
    var application = await ApplicationAsync();

    var (exit, _, _) = await RunAsync(application, "book", "--preset", "commute");

    Assert.Equal(CliExitCode.Success, exit);
    var request = Assert.IsType<ConversionRequest>(_service.Request);
    Assert.Equal("efficient", request.Options.QualityProfile.Id);
    Assert.Equal(ChannelPolicy.ForceMono, request.Options.Settings.ChannelPolicy);
    Assert.False(request.CreateChapters);
    Assert.Equal(ValidationLevel.Full, request.Options.Settings.ValidationLevel);
    Assert.Equal("{author}\\{title}", request.Options.Settings.OutputTemplate);
    Assert.Equal(_output, request.Options.DestinationDirectory);
    Assert.Equal(3, request.Options.Settings.ConversionJobs);
  }

  [Fact]
  public async Task ExplicitFlagsWinOverThePreset()
  {
    Presets.Save([Commute()]);
    var application = await ApplicationAsync();

    await RunAsync(application, "book", "--preset", "Commute", "--quality", "preserve-more", "--chapters", "--channels", "stereo", "--validation", "lightweight", "--output-template", "{title}");

    var request = Assert.IsType<ConversionRequest>(_service.Request);
    Assert.Equal("preserve-more", request.Options.QualityProfile.Id);
    Assert.True(request.CreateChapters);
    Assert.Equal(ChannelPolicy.ForceStereo, request.Options.Settings.ChannelPolicy);
    Assert.Equal(ValidationLevel.Lightweight, request.Options.Settings.ValidationLevel);
    Assert.Equal("{title}", request.Options.Settings.OutputTemplate);
  }

  [Fact]
  public async Task APresetWithoutATemplateOrFolderKeepsTheSavedOnes()
  {
    Presets.Save([Commute() with { OutputTemplate = null }]);
    var application = await ApplicationAsync(AppSettings.Defaults with { OutputDirectory = _output, OutputTemplate = "{series}\\{title}" });

    await RunAsync(application, "book", "--preset", "Commute");

    var request = Assert.IsType<ConversionRequest>(_service.Request);
    Assert.Equal("{series}\\{title}", request.Options.Settings.OutputTemplate);
    Assert.Equal(_output, request.Options.DestinationDirectory);
  }

  [Fact]
  public async Task PresetOutputFolderReplacesTheSavedOneAndAnOutputFlagReplacesItAgain()
  {
    var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
    Presets.Save([Commute() with { OutputDirectory = other }]);
    var application = await ApplicationAsync();

    await RunAsync(application, "book", "--preset", "Commute");
    Assert.Equal(other, _service.Request!.Options.DestinationDirectory);

    await RunAsync(application, "book", "--preset", "Commute", "--output", _output);
    Assert.Equal(_output, _service.Request!.Options.DestinationDirectory);
  }

  [Fact]
  public async Task UnknownPresetIsAConfigurationErrorNamingItAndNothingRuns()
  {
    Presets.Save([Commute()]);
    var application = await ApplicationAsync();

    var (exit, _, error) = await RunAsync(application, "book", "--preset", "Nope");

    Assert.Equal(CliExitCode.InvalidInput, exit);
    Assert.Contains("'Nope'", error);
    Assert.Contains("Commute", error);
    Assert.Null(_service.Request);
  }

  [Fact]
  public async Task InvalidPresetIsRefusedWithThePresetAndFieldEvenWhenAFlagWouldOverrideIt()
  {
    Presets.Save([Commute() with { QualityProfileId = "gone" }]);
    var application = await ApplicationAsync();

    var (exit, _, error) = await RunAsync(application, "book", "--preset", "Commute", "--quality", "efficient");

    Assert.Equal(CliExitCode.InvalidInput, exit);
    Assert.Contains("Preset 'Commute': qualityProfileId 'gone'", error);
    Assert.Null(_service.Request);
  }

  [Fact]
  public async Task UnusablePresetsFileIsReportedOnlyWhenAPresetIsRequested()
  {
    File.WriteAllText(PresetStore.DefaultPath(_root), "{ nope");
    var application = await ApplicationAsync();

    var (plain, _, _) = await RunAsync(application, "book");
    var (withPreset, _, error) = await RunAsync(application, "book", "--preset", "Commute");

    Assert.Equal(CliExitCode.Success, plain);
    Assert.Equal(CliExitCode.InvalidInput, withPreset);
    Assert.Contains("needs fixing", error);
    Assert.Equal("{ nope", File.ReadAllText(PresetStore.DefaultPath(_root)));
  }

  [Fact]
  public async Task EmptyPresetNameIsAUsageError()
  {
    var application = await ApplicationAsync();

    var (exit, _, error) = await RunAsync(application, "book", "--preset", "");

    Assert.Equal(CliExitCode.UsageError, exit);
    Assert.Contains("empty", error);
  }

  [Fact]
  public async Task ListShowsEachPresetAndFlagsTheOnesThatNeedFixing()
  {
    Presets.Save([Commute(), Commute("Broken") with { MetadataProfileId = "Missing" }]);
    var application = await ApplicationAsync();

    var (exit, output, _) = await RunAsync(application, "--list-presets");

    Assert.Equal(CliExitCode.Success, exit);
    Assert.Contains("Commute: quality efficient, channels ForceMono, chapters off, validation Full, metadata GenericMp4", output);
    Assert.Contains("Broken:", output);
    Assert.Contains("Needs fixing: Preset 'Broken': metadataProfileId 'Missing'", output);
    Assert.Null(_service.Request);
  }

  [Fact]
  public async Task ListJsonReportsValidityAndHidesTheOutputFolderPath()
  {
    Presets.Save([Commute() with { OutputDirectory = Path.Combine(_root, "private-place") }]);
    var application = await ApplicationAsync();

    var (exit, output, _) = await RunAsync(application, "--list-presets", "--json");

    Assert.Equal(CliExitCode.Success, exit);
    Assert.DoesNotContain("private-place", output);
    using var document = JsonDocument.Parse(output);
    var preset = document.RootElement.GetProperty("presets")[0];
    Assert.Equal("Commute", preset.GetProperty("name").GetString());
    Assert.True(preset.GetProperty("isValid").GetBoolean());
    Assert.True(preset.GetProperty("hasOutputDirectory").GetBoolean());
  }

  [Fact]
  public async Task ListWithNoSavedPresetsSaysSo()
  {
    var application = await ApplicationAsync();

    var (exit, output, _) = await RunAsync(application, "--list-presets");

    Assert.Equal(CliExitCode.Success, exit);
    Assert.Contains("No presets are saved.", output);
  }

  [Fact]
  public async Task ListOfACorruptFileExitsWithTheConfigurationCode()
  {
    File.WriteAllText(PresetStore.DefaultPath(_root), "{ nope");
    var application = await ApplicationAsync();

    var (exit, _, error) = await RunAsync(application, "--list-presets");

    Assert.Equal(CliExitCode.InvalidInput, exit);
    Assert.Contains("cannot be used", error);
  }

  [Fact]
  public void ListPresetsTakesNoSourceOrOtherOptions()
  {
    Assert.True(CliParser.Parse(["--list-presets"]).IsSuccess);
    Assert.True(CliParser.Parse(["--list-presets", "--json"]).IsSuccess);
    Assert.False(CliParser.Parse(["--list-presets", "book"]).IsSuccess);
    Assert.False(CliParser.Parse(["--list-presets", "--quality", "balanced"]).IsSuccess);
  }

  [Fact]
  public void HelpDocumentsThePresetOptions()
  {
    var help = CliHelp.Build(BookSplice.Core.Metadata.MetadataProfileCatalog.BuiltIn);

    Assert.Contains("--preset <name>", help);
    Assert.Contains("--list-presets", help);
  }

  private sealed class RecordingService : IConversionService
  {
    public ConversionRequest? Request { get; private set; }

    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      Request = request;
      return Task.FromResult(new ConversionServiceResult(Guid.NewGuid(), ConversionTerminalStatus.Succeeded, ConversionStage.Completed, null, null, null, null, null, [], [], null));
    }
  }
}
