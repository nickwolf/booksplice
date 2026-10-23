using BookSplice.Core.Metadata;
using BookSplice.Core.Settings;

namespace BookSplice.Cli.Tests;

public sealed class MetadataProfileCliTests
{
  private const string Custom = """{"schemaVersion":1,"name":"SyntheticCustom","version":"1","mappings":[{"field":"BookTitle","keys":["TITLE"]}]}""";

  [Fact]
  public async Task UnknownProfileIsAConfigurationErrorThatNamesTheProfile()
  {
    using var env = new Environment();
    var (exit, error) = await env.RunAsync("--metadata-profile", "NoSuchProfile");

    Assert.Equal(CliExitCode.InvalidInput, exit);
    Assert.Contains("NoSuchProfile", error);
    Assert.Contains("No such profile is installed.", error);
    Assert.Contains("GenericMp4", error);
    Assert.Contains("booksplice --help", error);
  }

  [Fact]
  public async Task ProfileFromARejectedFileReportsTheFileAndReason()
  {
    using var env = new Environment();
    env.WriteProfile("broken.json", """{"schemaVersion":1,"name":"BrokenProfile","version":"1","mappings":[]}""");
    var (exit, error) = await env.RunAsync("--metadata-profile", "BrokenProfile");

    Assert.Equal(CliExitCode.InvalidInput, exit);
    Assert.Contains("broken.json", error);
    Assert.Contains("must not be empty", error);
  }

  [Fact]
  public async Task OverrideWorksWhenTheSavedProfileIsMissing()
  {
    using var env = new Environment();
    env.WriteProfile("custom.json", Custom);
    await new JsonSettingsStore(env.LocalData).SaveAsync(AppSettings.Defaults with { OutputDirectory = env.Output, MetadataProfileId = "SavedButGone" });

    var (saved, savedError) = await env.RunAsync();
    var (overridden, _) = await env.RunAsync("--metadata-profile", "SyntheticCustom");

    Assert.Equal(CliExitCode.InvalidInput, saved);
    Assert.Contains("SavedButGone", savedError);
    Assert.Equal(CliExitCode.ExecutionFailure, overridden);
  }

  [Fact]
  public void HelpListsCustomProfilesAndRejectedFiles()
  {
    using var env = new Environment();
    env.WriteProfile("custom.json", Custom);
    env.WriteProfile("broken.json", """{"schemaVersion":1,"name":"BrokenProfile","version":"1","mappings":[]}""");

    var help = CliHelp.Build(MetadataProfileCatalog.Load(CliComposition.ProfilesDirectory(env.LocalData)));

    Assert.Contains("GenericMp4, SyntheticCustom", help);
    Assert.Contains("Skipped broken.json:", help);
    Assert.DoesNotContain("BrokenProfile,", help);
    Assert.Contains("Profiles folder: " + CliComposition.ProfilesDirectory(env.LocalData), CliHelp.Build(MetadataProfileCatalog.BuiltIn, CliComposition.ProfilesDirectory(env.LocalData)));
  }

  [Fact]
  public void HelpWithoutAProfilesFolderListsOnlyTheBuiltIn()
  {
    using var env = new Environment();

    Assert.Contains("Metadata profiles: GenericMp4", CliHelp.Build(MetadataProfileCatalog.Load(CliComposition.ProfilesDirectory(env.LocalData))));
  }

  private sealed class Environment : IDisposable
  {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"BookSplice-{Guid.NewGuid():N}");
    public Environment()
    {
      Directory.CreateDirectory(_root);
      LocalData = Path.Combine(_root, "local");
      Output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
      MissingTools = Directory.CreateDirectory(Path.Combine(_root, "missing-tools")).FullName;
    }
    public string LocalData { get; }
    public string Output { get; }
    public string MissingTools { get; }
    public void WriteProfile(string name, string json)
    {
      var directory = Directory.CreateDirectory(CliComposition.ProfilesDirectory(LocalData)).FullName;
      File.WriteAllText(Path.Combine(directory, name), json);
    }
    public async Task<(CliExitCode Exit, string Error)> RunAsync(params string[] extra)
    {
      var error = new StringWriter();
      var exit = await CliComposition.Create(LocalData, MissingTools).RunAsync(["book", "--output", Output, .. extra], TextWriter.Null, error, CancellationToken.None);
      return (exit, error.ToString());
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
  }
}
