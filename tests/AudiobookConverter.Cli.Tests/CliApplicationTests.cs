using System.Text.Json;
using AudiobookConverter.Cli;
using AudiobookConverter.Core.Analysis;
using AudiobookConverter.Core.Execution;
using AudiobookConverter.Core.Planning;
using AudiobookConverter.Core.Settings;

namespace AudiobookConverter.Cli.Tests;

public sealed class CliApplicationTests
{
  [Fact]
  public async Task JsonModeWritesOnlyVersionedEventsAndFinalExitCodeToStandardOutput()
  {
    var service = new FakeService(Result(ConversionTerminalStatus.ValidationFailed));
    var output = new StringWriter();
    var error = new StringWriter();
    var application = new CliApplication(new SettingsStore(Settings()), service);

    var exitCode = await application.RunAsync(["book", "--json"], output, error, CancellationToken.None);

    Assert.Equal(CliExitCode.ValidationFailure, exitCode);
    var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    Assert.NotEmpty(lines);
    var events = lines.Select(line => JsonDocument.Parse(line)).ToArray();
    Assert.All(events, item => Assert.Equal(1, item.RootElement.GetProperty("schemaVersion").GetInt32()));
    Assert.All(events, item => Assert.True(item.RootElement.TryGetProperty("event", out _)));
    var final = events[^1].RootElement;
    Assert.Equal("final", final.GetProperty("event").GetString());
    Assert.Equal("ValidationFailed", final.GetProperty("status").GetString());
    Assert.Equal(6, final.GetProperty("exitCode").GetInt32());
    Assert.DoesNotContain("Conversion", output.ToString(), StringComparison.Ordinal);
  }

  [Fact]
  public async Task DryRunJsonIncludesOrderStrategyOutputAndPredicates()
  {
    var plan = Plan();
    var planning = new ConversionPlanningResult(BookAnalysisStatus.Ready, plan, []);
    var result = Result(ConversionTerminalStatus.DryRun) with { Planning = planning };
    var output = new StringWriter();
    var application = new CliApplication(new SettingsStore(Settings()), new FakeService(result));

    var exitCode = await application.RunAsync(["book", "--dry-run", "--json"], output, TextWriter.Null, CancellationToken.None);

    Assert.Equal(CliExitCode.Success, exitCode);
    var finalLine = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[^1];
    using var final = JsonDocument.Parse(finalLine);
    Assert.Equal(plan.SourcePaths, final.RootElement.GetProperty("plan").GetProperty("orderedSources").EnumerateArray().Select(value => value.GetString()));
    Assert.Equal("DirectTranscode", final.RootElement.GetProperty("plan").GetProperty("strategy").GetString());
    Assert.Equal(plan.OutputPath, final.RootElement.GetProperty("plan").GetProperty("outputPath").GetString());
    Assert.Equal("strategy.test", final.RootElement.GetProperty("plan").GetProperty("predicates")[0].GetString());
  }

  [Fact]
  public async Task ParseErrorsReturnUsageCodeWithoutCallingService()
  {
    var service = new FakeService(Result(ConversionTerminalStatus.Succeeded));
    var error = new StringWriter();
    var application = new CliApplication(new SettingsStore(Settings()), service);

    var exitCode = await application.RunAsync(["book", "--unknown"], TextWriter.Null, error, CancellationToken.None);

    Assert.Equal(CliExitCode.UsageError, exitCode);
    Assert.False(service.WasCalled);
    Assert.Contains("Unknown option", error.ToString(), StringComparison.Ordinal);
  }

  [Fact]
  public async Task CommandLineOverridesReachTheSharedServiceWithoutSavingSettings()
  {
    var store = new SettingsStore(Settings());
    var service = new FakeService(Result(ConversionTerminalStatus.Succeeded));
    var application = new CliApplication(store, service);

    await application.RunAsync(["book", "--output", "C:\\Override", "--quality", "efficient", "--bitrate", "75", "--jobs", "2", "--no-chapters", "--overwrite"], TextWriter.Null, TextWriter.Null, CancellationToken.None);

    var request = Assert.IsType<ConversionRequest>(service.Request);
    Assert.Equal("C:\\Override", request.Options.DestinationDirectory);
    Assert.Equal("custom", request.Options.QualityProfile.Id);
    Assert.Equal(75, request.Options.QualityProfile.AudioBitrateKbps);
    Assert.Equal(2, request.Options.Settings.ConversionJobs);
    Assert.False(request.CreateChapters);
    Assert.Equal(Core.Naming.CollisionPolicy.Overwrite, request.Options.CollisionPolicy);
    Assert.Equal(0, store.SaveCount);
  }

  private static ConversionServiceResult Result(ConversionTerminalStatus status) => new(
    Guid.NewGuid(), status, ConversionStage.Completed, null, null, null, null, null,
    [new("test", ServiceDiagnosticSeverity.Error, "diagnostic")], [], null);

  private static ConversionPlan Plan() => new(
    ["C:\\Book\\01.mp3", "C:\\Book\\02.mp3"],
    new(new Dictionary<Core.Metadata.SemanticField, Core.Metadata.AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<Core.Metadata.SemanticField, IReadOnlyList<string>>()),
    null, [], QualityProfileCatalog.Version1[0], ValidationLevel.Lightweight, Core.Naming.CollisionPolicy.AvoidCollision,
    "C:\\Output\\Book.m4b", AudioStrategy.DirectTranscode, ["strategy.test"], new(1, 1, 2, 3, 1), 1, "test", "GenericMp4");

  private static AppSettings Settings() => AppSettings.Defaults with { OutputDirectory = "C:\\Output" };

  private sealed class FakeService(ConversionServiceResult result) : IConversionService
  {
    public bool WasCalled { get; private set; }
    public ConversionRequest? Request { get; private set; }
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      WasCalled = true;
      Request = request;
      progress?.Report(new(500_000, 2.0, "continue", 0, null, 50));
      return Task.FromResult(result);
    }
  }

  private sealed class SettingsStore(AppSettings settings) : ISettingsStore
  {
    public int SaveCount { get; private set; }
    public string SettingsPath => "settings.json";
    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SettingsLoadResult(SettingsLoadCode.Valid, settings, []));
    public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) { SaveCount++; return Task.CompletedTask; }
  }
}
