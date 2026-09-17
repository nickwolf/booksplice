using BookSplice.Cli;
using BookSplice.Core.Analysis;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;

namespace BookSplice.Cli.Tests;

public sealed class CliConfigurationTests
{
  [Fact]
  public void ResolveAppliesAllOverridesWithoutMutatingLoadedSettingsOrCatalog()
  {
    var loaded = Settings("C:\\Saved", "balanced", 3, createChapters: true);
    var options = new CliOptions("book", "C:\\Override", "efficient", 111, 9, false, true, false, false);

    var result = CliConfiguration.Resolve(options, new(SettingsLoadCode.Valid, loaded, []));

    Assert.True(result.IsSuccess);
    Assert.Equal("C:\\Override", result.Settings!.OutputDirectory);
    Assert.Equal("custom", result.QualityProfile!.Id);
    Assert.Equal("Custom", result.QualityProfile.DisplayName);
    Assert.Equal(111, result.QualityProfile.AudioBitrateKbps);
    Assert.Equal(QualityProfileCatalog.Version1.Single(profile => profile.Id == "efficient") with { Id = "custom", DisplayName = "Custom", AudioBitrateKbps = 111 }, result.QualityProfile);
    Assert.Equal(9, result.Settings.ConversionJobs);
    Assert.False(result.Settings.CreateChapters);
    Assert.Equal(CollisionPolicy.Overwrite, result.Settings.CollisionPolicy);
    Assert.Equal("C:\\Saved", loaded.OutputDirectory);
    Assert.Equal(48, QualityProfileCatalog.Version1.Single(profile => profile.Id == "efficient").AudioBitrateKbps);
  }

  [Fact]
  public void ResolveUsesSafeDefaultsWhenSettingsAreMissingAndOutputIsProvided()
  {
    var options = new CliOptions("book", "C:\\Output", null, null, null, null, false, false, false);

    var result = CliConfiguration.Resolve(options, SettingsLoadResult.Missing(AppSettings.Defaults));

    Assert.True(result.IsSuccess);
    Assert.Equal("C:\\Output", result.Settings!.OutputDirectory);
    Assert.Equal("high-quality", result.QualityProfile!.Id);
    Assert.True(result.Settings.CreateChapters);
    Assert.Equal(CollisionPolicy.AvoidCollision, result.Settings.CollisionPolicy);
  }

  [Fact]
  public void ResolveForcesCollisionSafePolicyUnlessOverwriteWasRequested()
  {
    var loaded = Settings("C:\\Saved", "balanced", 3, createChapters: true) with { CollisionPolicy = CollisionPolicy.Overwrite };
    var options = new CliOptions("book", "C:\\Output", null, null, null, null, false, false, false);

    var result = CliConfiguration.Resolve(options, new(SettingsLoadCode.Valid, loaded, []));

    Assert.True(result.IsSuccess);
    Assert.Equal(CollisionPolicy.AvoidCollision, result.Settings!.CollisionPolicy);
  }

  [Fact]
  public void ResolveRejectsMissingEffectiveOutput()
  {
    var options = new CliOptions("book", null, null, null, null, null, false, false, false);

    var result = CliConfiguration.Resolve(options, SettingsLoadResult.Missing(AppSettings.Defaults));

    Assert.False(result.IsSuccess);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "configuration.output-required");
  }

  [Fact]
  public void ResolveRejectsCorruptSettingsEvenWhenArgumentsContainAnOutput()
  {
    var options = new CliOptions("book", "C:\\Output", null, null, null, null, false, false, false);

    var result = CliConfiguration.Resolve(options, new(SettingsLoadCode.Corrupt, null, ["bad json"]));

    Assert.False(result.IsSuccess);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "configuration.settings-invalid");
  }

  private static AppSettings Settings(string output, string quality, int jobs, bool createChapters) => new(
    AppSettings.CurrentSchemaVersion, output, quality, ChannelPolicy.PreserveSourceChannels, createChapters, jobs,
    CollisionPolicy.AvoidCollision, "GenericMp4", ValidationLevel.Lightweight, LogLevel.Information);
}

public sealed class CliExitCodeTests
{
  public static TheoryData<ConversionTerminalStatus, CliExitCode> Mappings => new()
  {
    { ConversionTerminalStatus.Succeeded, CliExitCode.Success },
    { ConversionTerminalStatus.DryRun, CliExitCode.Success },
    { ConversionTerminalStatus.InvalidInput, CliExitCode.InvalidInput },
    { ConversionTerminalStatus.DecisionRequired, CliExitCode.OrderingDecisionRequired },
    { ConversionTerminalStatus.ExecutionFailed, CliExitCode.ExecutionFailure },
    { ConversionTerminalStatus.ValidationFailed, CliExitCode.ValidationFailure },
    { ConversionTerminalStatus.PublicationFailed, CliExitCode.PublicationFailure },
    { ConversionTerminalStatus.Cancelled, CliExitCode.Cancelled },
    { ConversionTerminalStatus.UnexpectedFailure, CliExitCode.UnexpectedFailure },
  };

  [Theory]
  [MemberData(nameof(Mappings))]
  public void FromStatusReturnsStableMapping(ConversionTerminalStatus status, CliExitCode expected)
    => Assert.Equal(expected, CliExitCodes.FromStatus(status));

  [Fact]
  public void NumericValuesAreStable()
    => Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8], Enum.GetValues<CliExitCode>().Select(value => (int)value));
}
