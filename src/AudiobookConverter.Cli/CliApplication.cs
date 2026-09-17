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
  private readonly IJobLogWriter? _logs;

  public CliApplication(ISettingsStore settings, IConversionService service, IJobLogWriter? logs = null)
    => (_settings, _service, _logs) = (settings, service, logs);

  public async Task<CliExitCode> RunAsync(IReadOnlyList<string> arguments, TextWriter standardOutput, TextWriter standardError, CancellationToken cancellationToken)
  {
    var parsed = CliParser.Parse(arguments);
    var requestedJson = arguments.Contains("--json", StringComparer.Ordinal);
    var requestedDryRun = arguments.Contains("--dry-run", StringComparer.Ordinal);
    if (!parsed.IsSuccess)
    {
      return await CompletePreServiceAsync(
        ConversionTerminalStatus.InvalidInput,
        CliExitCode.UsageError,
        requestedJson,
        requestedDryRun,
        parsed.Diagnostics.Select(value => new ServiceDiagnostic(value.Code, ServiceDiagnosticSeverity.Error, value.Message)),
        standardOutput,
        standardError).ConfigureAwait(false);
    }

    SettingsLoadResult loaded;
    try { loaded = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false); }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      return await CompletePreServiceAsync(
        ConversionTerminalStatus.Cancelled,
        CliExitCode.Cancelled,
        requestedJson,
        requestedDryRun,
        [new("conversion.cancelled", ServiceDiagnosticSeverity.Information, "The conversion was cancelled.")],
        standardOutput,
        standardError).ConfigureAwait(false);
    }
    catch (Exception)
    {
      return await CompletePreServiceAsync(
        ConversionTerminalStatus.UnexpectedFailure,
        CliExitCode.UnexpectedFailure,
        requestedJson,
        requestedDryRun,
        [new("settings.load-failed", ServiceDiagnosticSeverity.Error, "The saved settings could not be loaded.")],
        standardOutput,
        standardError).ConfigureAwait(false);
    }

    var configured = CliConfiguration.Resolve(parsed.Options!, loaded);
    if (!configured.IsSuccess)
    {
      return await CompletePreServiceAsync(
        ConversionTerminalStatus.InvalidInput,
        CliExitCode.InvalidInput,
        requestedJson,
        requestedDryRun,
        configured.Diagnostics.Select(value => new ServiceDiagnostic(value.Code, ServiceDiagnosticSeverity.Error, value.Message)),
        standardOutput,
        standardError).ConfigureAwait(false);
    }

    var options = parsed.Options!;
    var settings = configured.Settings!;
    var request = new ConversionRequest(
      options.Source,
      settings.CreateChapters,
      new ConversionOptions(settings, configured.QualityProfile!, destinationDirectory: settings.OutputDirectory, collisionPolicy: options.Overwrite ? CollisionPolicy.Overwrite : settings.CollisionPolicy),
      options.DryRun);
    var sync = new object();
    if (options.Json) WriteJson(standardOutput, new { SchemaVersion = 1, Event = "started" }, sync);
    else standardOutput.WriteLine("Analyzing audiobook");

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
      return await CompletePreServiceAsync(
        ConversionTerminalStatus.Cancelled,
        CliExitCode.Cancelled,
        options.Json,
        options.DryRun,
        [new("conversion.cancelled", ServiceDiagnosticSeverity.Information, "The conversion was cancelled.")],
        standardOutput,
        standardError).ConfigureAwait(false);
    }
    catch (Exception)
    {
      return await CompletePreServiceAsync(
        ConversionTerminalStatus.UnexpectedFailure,
        CliExitCode.UnexpectedFailure,
        options.Json,
        options.DryRun,
        [new("conversion.unexpected", ServiceDiagnosticSeverity.Error, "The conversion failed unexpectedly.")],
        standardOutput,
        standardError).ConfigureAwait(false);
    }

    var exitCode = CliExitCodes.FromStatus(result.Status);
    WriteDiagnostics(standardError, result.Diagnostics.Select(value => value.Message), [options.Source]);
    if (options.Json)
    {
      var plan = result.Planning?.Plan;
      var dryRunPlan = plan is null ? null : new
      {
        Sources = plan.SourcePaths.Select((_, index) => $"source-{index + 1}"),
        Strategy = plan.Strategy.ToString(),
        plan.OutputPath,
        Warnings = result.Diagnostics.Where(value => value.Severity == ServiceDiagnosticSeverity.Warning).Select(value => PublicTextRedactor.Sanitize(value.Message, plan.SourcePaths)),
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
        for (var index = 0; index < plan.SourcePaths.Count; index++) standardOutput.WriteLine($"Source {index + 1}");
      }
      else if (result.Status == ConversionTerminalStatus.Succeeded) standardOutput.WriteLine($"Published {result.PublishedPath}");
    }
    return exitCode;
  }

  private async Task<CliExitCode> CompletePreServiceAsync(
    ConversionTerminalStatus status,
    CliExitCode exitCode,
    bool json,
    bool dryRun,
    IEnumerable<ServiceDiagnostic> sourceDiagnostics,
    TextWriter standardOutput,
    TextWriter standardError)
  {
    var diagnostics = sourceDiagnostics.ToList();
    if (!dryRun && _logs is not null)
    {
      var jobId = Guid.NewGuid();
      var timestamp = DateTimeOffset.UtcNow;
      var record = new ConversionAuditRecord(
        ConversionAuditRecord.CurrentSchemaVersion,
        jobId,
        timestamp,
        timestamp,
        status,
        null,
        null,
        null,
        null,
        null,
        [],
        null,
        null,
        null,
        null,
        [],
        diagnostics,
        null);
      try { await _logs.WriteAsync(record, CancellationToken.None).ConfigureAwait(false); }
      catch (Exception)
      {
        diagnostics.Add(new("audit.write-failed", ServiceDiagnosticSeverity.Warning, "The job audit record could not be written."));
      }
    }
    WriteDiagnostics(standardError, diagnostics.Select(value => value.Message));
    if (json) WriteJson(standardOutput, new { SchemaVersion = 1, Event = "final", Status = status.ToString(), ExitCode = (int)exitCode }, new object());
    return exitCode;
  }

  private static void WriteDiagnostics(TextWriter writer, IEnumerable<string> diagnostics, IReadOnlyList<string>? exactPaths = null)
  {
    foreach (var diagnostic in diagnostics) writer.WriteLine(PublicTextRedactor.Sanitize(diagnostic, exactPaths));
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
