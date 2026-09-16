using System.Text.Json;
using AudiobookConverter.Core.Analysis;
using AudiobookConverter.Core.Execution;
using AudiobookConverter.Core.Naming;
using AudiobookConverter.Core.Planning;
using AudiobookConverter.Core.Settings;

namespace AudiobookConverter.Cli;

public sealed class CliApplication
{
  private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
  private readonly ISettingsStore _settings;
  private readonly IConversionService _service;

  public CliApplication(ISettingsStore settings, IConversionService service)
    => (_settings, _service) = (settings, service);

  public async Task<CliExitCode> RunAsync(IReadOnlyList<string> arguments, TextWriter standardOutput, TextWriter standardError, CancellationToken cancellationToken)
  {
    var parsed = CliParser.Parse(arguments);
    if (!parsed.IsSuccess)
    {
      WriteDiagnostics(standardError, parsed.Diagnostics.Select(value => value.Message));
      return CliExitCode.UsageError;
    }

    SettingsLoadResult loaded;
    try { loaded = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false); }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return CliExitCode.Cancelled; }
    catch (Exception)
    {
      standardError.WriteLine("The saved settings could not be loaded.");
      return CliExitCode.UnexpectedFailure;
    }

    var configured = CliConfiguration.Resolve(parsed.Options!, loaded);
    if (!configured.IsSuccess)
    {
      WriteDiagnostics(standardError, configured.Diagnostics.Select(value => value.Message));
      return CliExitCode.InvalidInput;
    }

    var options = parsed.Options!;
    var settings = configured.Settings!;
    var request = new ConversionRequest(
      options.Source,
      settings.CreateChapters,
      new ConversionOptions(settings, configured.QualityProfile!, destinationDirectory: settings.OutputDirectory, collisionPolicy: options.Overwrite ? CollisionPolicy.Overwrite : settings.CollisionPolicy),
      options.DryRun);
    var sync = new object();
    if (options.Json) WriteJson(standardOutput, new { SchemaVersion = 1, Event = "started", Source = options.Source }, sync);
    else standardOutput.WriteLine($"Analyzing {options.Source}");

    var progress = new InlineProgress<ConversionProgress>(value =>
    {
      if (options.Json)
        WriteJson(standardOutput, new { SchemaVersion = 1, Event = "progress", value.DisplayPercent, value.Speed, value.StageIndex, value.SourceIndex, value.State }, sync);
      else if (value.DisplayPercent is { } percent)
        standardOutput.WriteLine($"Progress {percent:0.#}%");
    });

    ConversionServiceResult result;
    try { result = await _service.ConvertAsync(request, cancellationToken, progress).ConfigureAwait(false); }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      result = new(Guid.NewGuid(), ConversionTerminalStatus.Cancelled, ConversionStage.Completed, null, null, null, null, null, [], [], null);
    }
    catch (Exception)
    {
      standardError.WriteLine("The conversion failed unexpectedly.");
      return CliExitCode.UnexpectedFailure;
    }

    var exitCode = CliExitCodes.FromStatus(result.Status);
    WriteDiagnostics(standardError, result.Diagnostics.Select(value => value.Message));
    if (options.Json)
    {
      var plan = result.Planning?.Plan;
      var dryRunPlan = plan is null ? null : new
      {
        OrderedSources = plan.SourcePaths,
        Strategy = plan.Strategy.ToString(),
        plan.OutputPath,
        Warnings = result.Diagnostics.Where(value => value.Severity == ServiceDiagnosticSeverity.Warning).Select(value => value.Message),
        Predicates = plan.StrategyReasonCodes,
      };
      WriteJson(standardOutput, new { SchemaVersion = 1, Event = "final", Status = result.Status.ToString(), ExitCode = (int)exitCode, result.JobId, result.PublishedPath, Plan = dryRunPlan }, sync);
    }
    else
    {
      if (result.Status == ConversionTerminalStatus.DryRun && result.Planning?.Plan is { } plan)
      {
        standardOutput.WriteLine($"Strategy: {plan.Strategy}");
        standardOutput.WriteLine($"Output: {plan.OutputPath}");
        foreach (var source in plan.SourcePaths) standardOutput.WriteLine(source);
      }
      else if (result.Status == ConversionTerminalStatus.Succeeded) standardOutput.WriteLine($"Published {result.PublishedPath}");
    }
    return exitCode;
  }

  private static void WriteDiagnostics(TextWriter writer, IEnumerable<string> diagnostics)
  {
    foreach (var diagnostic in diagnostics) writer.WriteLine(diagnostic);
  }

  private static void WriteJson(TextWriter writer, object value, object sync)
  {
    lock (sync) writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
  }

  private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
  {
    public void Report(T value) => action(value);
  }
}
