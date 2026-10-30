using System.Text.Json;
using BookSplice.Core.Analysis;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Presets;
using BookSplice.Core.Settings;

namespace BookSplice.Cli;

public sealed class CliApplication
{
  private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
  private readonly ISettingsStore _settings;
  private readonly IConversionService _service;
  private readonly IJobLogWriter? _logs;
  private readonly TimeProvider _timeProvider;

  private readonly MetadataProfileCatalog? _profiles;

  private readonly PresetStore? _presets;

  public CliApplication(ISettingsStore settings, IConversionService service, IJobLogWriter? logs = null, MetadataProfileCatalog? profiles = null, TimeProvider? timeProvider = null, PresetStore? presets = null)
    => (_settings, _service, _logs, _profiles, _timeProvider, _presets) = (settings, service, logs, profiles, timeProvider ?? TimeProvider.System, presets);

  public async Task<CliExitCode> RunAsync(IReadOnlyList<string> arguments, TextWriter standardOutput, TextWriter standardError, CancellationToken cancellationToken, TextReader? standardInput = null)
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

    if (parsed.Options!.ListPresets) return ListPresets(parsed.Options.Json, standardOutput, standardError);

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

    var presets = parsed.Options!.PresetName is not null ? _presets?.Load() : null;
    var configured = CliConfiguration.Resolve(parsed.Options!, loaded, _profiles, presets);
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
      options.DryRun, new BookAnalysisOptions(options.Order));
    var sync = new object();
    if (options.Json) WriteJson(standardOutput, new { SchemaVersion = 1, Event = "started" }, sync);
    else standardOutput.WriteLine("Analyzing audiobook");

    ConversionServiceResult result;
    try
    {
      result = await RunAttemptAsync(request, standardOutput, options.Json, cancellationToken).ConfigureAwait(false);
      if (result.Status == ConversionTerminalStatus.DecisionRequired && !options.Json && standardInput is not null && result.Analysis?.OrderResolution is { } resolution)
      {
        var candidates = resolution.Candidates.Where(candidate => candidate.IsCredible).ToArray();
        for (var index = 0; index < candidates.Length; index++)
        {
          standardOutput.WriteLine($"{index + 1}: {candidates[index].Id}");
          foreach (var file in candidates[index].FileIds) standardOutput.WriteLine($"  {PublicTextRedactor.Sanitize(file)}");
        }
        standardOutput.WriteLine("Select an order number, or press Enter to stop:");
        var answer = await ReadLineWithCancellationAsync(standardInput, cancellationToken).ConfigureAwait(false);
        if (int.TryParse(answer, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var selection) && selection >= 1 && selection <= candidates.Length)
        {
          var candidate = candidates[selection - 1];
          request = request with
          {
            AnalysisOptions = new BookAnalysisOptions(candidate.Id),
            ExpectedSourcePaths = candidate.FileIds.Select(file => Path.Combine(result.Analysis.SourceRootPath, file)).ToArray()
          };
          result = await RunAttemptAsync(request, standardOutput, options.Json, cancellationToken).ConfigureAwait(false);
        }
      }
    }
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
    var notesToOutput = !options.Json;
    WriteDiagnostics(standardError, result.Diagnostics.Where(value => !notesToOutput || !IsNote(value)).Select(value => value.Message), [options.Source]);
    if (notesToOutput) WriteDiagnostics(standardOutput, result.Diagnostics.Where(IsNote).Select(value => value.Message), [options.Source]);
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
        StrategyExplanation = StrategyExplanation.Create(plan.Strategy, plan.StrategyReasonCodes),
        Space = new { plan.Space.FinalBytes, plan.Space.TemporaryBytes, plan.Space.TotalRequiredBytes, plan.Space.AvailableBytes, plan.Space.DestinationVolume, plan.Space.TemporaryVolume, plan.Space.DestinationRequiredBytes, plan.Space.TemporaryRequiredBytes, plan.Space.TemporaryAvailableBytes },
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
        var explanation = StrategyExplanation.Create(plan.Strategy, plan.StrategyReasonCodes);
        standardOutput.WriteLine(explanation.Summary);
        foreach (var reason in explanation.Reasons) standardOutput.WriteLine($"  - {reason}");
        foreach (var line in SpaceEstimateDescription.Lines(plan.Space)) standardOutput.WriteLine(line);
      }
      else if (result.Status == ConversionTerminalStatus.Succeeded) standardOutput.WriteLine($"Published {result.PublishedPath}");
    }
    return exitCode;
  }

  private CliExitCode ListPresets(bool json, TextWriter standardOutput, TextWriter standardError)
  {
    var loaded = _presets?.Load();
    if (loaded is null || loaded.Code is PresetLoadCode.Corrupt or PresetLoadCode.UnsupportedFutureSchema)
    {
      standardError.WriteLine(loaded is null ? "Presets are not available." : $"The presets file cannot be used: {string.Join(" ", loaded.Errors)}");
      if (json) WriteJson(standardOutput, new { SchemaVersion = 1, Event = "final", Status = "InvalidInput", ExitCode = (int)CliExitCode.InvalidInput }, new object());
      return CliExitCode.InvalidInput;
    }
    var catalog = _profiles ?? MetadataProfileCatalog.BuiltIn;
    var rows = loaded.Presets.Select(preset => (Preset: preset, Errors: PresetValidator.Validate(preset, catalog))).ToArray();
    if (json)
    {
      var presets = rows.Select(row => new
      {
        row.Preset.Name,
        QualityProfileId = row.Preset.QualityProfileId,
        ChannelPolicy = row.Preset.ChannelPolicy.ToString(),
        row.Preset.CreateChapters,
        ValidationLevel = row.Preset.ValidationLevel.ToString(),
        row.Preset.MetadataProfileId,
        row.Preset.OutputTemplate,
        HasOutputDirectory = row.Preset.OutputDirectory is not null,
        IsValid = row.Errors.Count == 0,
        row.Errors,
      });
      WriteJson(standardOutput, new { SchemaVersion = 1, Event = "presets", Presets = presets, FileProblems = loaded.Errors }, new object());
      return CliExitCode.Success;
    }
    if (rows.Length == 0 && loaded.Errors.Count == 0) standardOutput.WriteLine("No presets are saved.");
    foreach (var (preset, errors) in rows)
    {
      var template = preset.OutputTemplate is null ? "" : $", template {preset.OutputTemplate}";
      var folder = preset.OutputDirectory is null ? "" : ", sets the output folder";
      standardOutput.WriteLine($"{preset.Name}: quality {preset.QualityProfileId}, channels {preset.ChannelPolicy}, chapters {(preset.CreateChapters ? "on" : "off")}, validation {preset.ValidationLevel}, metadata {preset.MetadataProfileId}{template}{folder}");
      foreach (var error in errors) standardOutput.WriteLine($"  Needs fixing: {error}");
    }
    foreach (var error in loaded.Errors) standardOutput.WriteLine($"Needs fixing: {error}");
    return CliExitCode.Success;
  }

  private async Task<ConversionServiceResult> RunAttemptAsync(ConversionRequest request, TextWriter output, bool json, CancellationToken cancellationToken)
  {
    await using var progress = new CliProgressDisplay(output, json, _timeProvider);
    progress.StartAttempt();
    try { return await _service.ConvertAsync(request, cancellationToken, progress).ConfigureAwait(false); }
    finally { await progress.StopAttemptAsync().ConfigureAwait(false); }
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

  private static readonly HashSet<string> NoteCodes = new(StringComparer.Ordinal)
  {
    "validation.note",
    "output.folder-skipped",
    "output.field-missing",
    "output.filename-fallback",
    "output.collision-renamed",
    "output.collision-overwrite",
  };

  private static bool IsNote(ServiceDiagnostic diagnostic) => NoteCodes.Contains(diagnostic.Code);

  private static void WriteDiagnostics(TextWriter writer, IEnumerable<string> diagnostics, IReadOnlyList<string>? exactPaths = null)
  {
    foreach (var diagnostic in diagnostics) writer.WriteLine(PublicTextRedactor.Sanitize(diagnostic, exactPaths));
  }

  private static void WriteJson(TextWriter writer, object value, object sync)
  {
    lock (sync) writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
  }

  private static Task<string?> ReadLineWithCancellationAsync(TextReader reader, CancellationToken cancellationToken)
    => Task.Run(reader.ReadLine, CancellationToken.None).WaitAsync(cancellationToken);

}
