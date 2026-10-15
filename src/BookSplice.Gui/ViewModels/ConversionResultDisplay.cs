using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BookSplice.Core.Analysis;
using BookSplice.Core.Settings;
using BookSplice.Core.Validation;

namespace BookSplice.Gui.ViewModels;

public sealed record ResultWarning(string Message, string? Explanation);

public sealed class ConversionResultDisplay
{
  private static readonly string[] RequiredAudioChecks = ["audio.primary-count", "audio.codec", "audio.sample-rate", "audio.layout", "audio.extra"];
  public string Title { get; private init; } = "";
  public string Summary { get; private init; } = "";
  public string? PublishedPath { get; private init; }
  public bool CanOpenOutput => PublishedPath is not null;
  public string OutputText => PublishedPath is { } path ? $"Saved to: {path}" : "No output was published.";
  public string ElapsedText { get; private init; } = "";
  public string ValidationText { get; private init; } = "";
  public string DecodeText { get; private init; } = "";
  public IReadOnlyList<ResultWarning> Warnings { get; private init; } = [];
  public bool HasWarnings => Warnings.Count > 0;
  public IReadOnlyList<string> Checks { get; private init; } = [];
  public string DiagnosticsText { get; private init; } = "";
  public string Details => string.Join(Environment.NewLine, new[] { Title, Summary, OutputText, ElapsedText, ValidationText, DecodeText }.Where(value => value.Length > 0));

  public static ConversionResultDisplay Create(ConversionServiceResult result, TimeSpan elapsed, ValidationLevel requestedLevel)
  {
    ArgumentNullException.ThrowIfNull(result);
    var report = result.Validation;
    var level = result.Planning?.Plan?.ValidationLevel ?? requestedLevel;
    var warningMessages = result.Diagnostics.Where(diagnostic => diagnostic.Severity == ServiceDiagnosticSeverity.Warning).Select(diagnostic => diagnostic.Message)
      .Concat(report?.Warnings ?? []).Distinct(StringComparer.Ordinal).ToArray();
    var warnings = warningMessages.Select(message => new ResultWarning(message, ExplainProbeWarning(message, report))).ToArray();
    var published = result.Publication is { Status: PublicationStatus.Published, FinalPath: { } finalPath } ? finalPath : result.PublishedPath;
    var succeeded = result.Status == ConversionTerminalStatus.Succeeded;
    var failedChecks = report?.Checks.Where(check => check.Required && !check.Passed).ToArray() ?? [];
    var reason = (failedChecks.Length > 0 ? failedChecks[0].Message : null) ?? result.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == ServiceDiagnosticSeverity.Error)?.Message
      ?? (report?.Errors.Count > 0 ? report.Errors[0] : null);
    var decode = report?.Checks.FirstOrDefault(check => check.Code == "decode.full");
    var baseChecks = report?.Checks.Where(check => check.Required && check.Code != "decode.full").ToArray() ?? [];
    var checkState = report is null ? "Not run" : baseChecks.Any(check => !check.Passed) ? "Failed" : baseChecks.Length > 0 ? "Passed" : "Not completed";
    var display = new ConversionResultDisplay
    {
      Title = succeeded ? warnings.Length > 0 ? "Conversion complete with warnings" : "Conversion complete"
        : result.Status == ConversionTerminalStatus.Cancelled ? "Conversion cancelled" : "Conversion failed",
      Summary = succeeded ? "" : result.Status == ConversionTerminalStatus.Cancelled ? "The job stopped before completion."
        : $"{result.Stage}: {reason ?? DescribeStatus(result.Status)}",
      PublishedPath = published,
      ElapsedText = ConversionProgressDisplay.ElapsedText(elapsed),
      ValidationText = $"Lightweight checks: {checkState}",
      DecodeText = "Full audio decode: " + (decode is not null ? decode.Passed ? "Passed" : "Failed" : level == ValidationLevel.Full && report is not null ? "Not completed" : "Not run"),
      Warnings = Array.AsReadOnly(warnings),
      Checks = Array.AsReadOnly((report?.Checks.Select(DescribeCheck) ?? []).ToArray()),
      DiagnosticsText = BuildDiagnostics(result),
    };
    return display;
  }

  private static string DescribeStatus(ConversionTerminalStatus status) => status switch
  {
    ConversionTerminalStatus.InvalidInput => "Review the input and conversion settings.",
    ConversionTerminalStatus.DecisionRequired => "Review the source files and track order before converting.",
    ConversionTerminalStatus.ExecutionFailed => "Audio conversion did not finish.",
    ConversionTerminalStatus.ValidationFailed => "The output did not pass validation.",
    ConversionTerminalStatus.PublicationFailed => "The validated output could not be saved to the destination.",
    _ => "The job did not finish successfully.",
  };

  private static string DescribeCheck(ValidationCheck check)
    => !check.Required ? $"Not required: {check.Code}. {check.Message}"
      : check.Passed ? $"Passed: {check.Code}" : $"Failed: {check.Code}. {check.Message}";

  private static string? ExplainProbeWarning(string message, ValidationReport? report)
  {
    if (report is not { IsValid: true, OutputFacts: { } facts } || !report.Warnings.Contains(message, StringComparer.Ordinal) || facts.AudioStreams.Count != 1 ||
        !RequiredAudioChecks.All(code => report.Checks.Any(check => check.Code == code && check.Required && check.Passed))) return null;
    var match = Regex.Match(message.Trim(), @"^Unsupported codec with id [0-9]+ for input stream (?<index>[0-9]+)$", RegexOptions.CultureInvariant);
    if (!match.Success || !int.TryParse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return null;
    var candidates = facts.OtherStreams?.Where(value => value.Index == index).ToArray() ?? [];
    var stream = candidates.Length == 1 ? candidates[0] : null;
    return stream is not null && string.Equals(stream.CodecType, "data", StringComparison.OrdinalIgnoreCase) && facts.AudioStreams[0].Index != index
      ? $"ffprobe reported an unsupported codec on data stream {index}. The required audio checks passed." : null;
  }

  private static string BuildDiagnostics(ConversionServiceResult result)
  {
    var text = new StringBuilder();
    foreach (var diagnostic in result.Diagnostics) text.AppendLine(CultureInfo.InvariantCulture, $"{diagnostic.Severity} [{diagnostic.Code}]: {diagnostic.Message}");
    if (result.Validation is { } report)
    {
      foreach (var warning in report.Warnings) text.AppendLine(CultureInfo.InvariantCulture, $"Validation warning: {warning}");
      foreach (var error in report.Errors) text.AppendLine(CultureInfo.InvariantCulture, $"Validation error: {error}");
      foreach (var output in report.ToolDiagnostics) text.AppendLine("Validation tool output:").AppendLine(output);
    }
    foreach (var process in result.Execution?.Processes ?? [])
    {
      text.AppendLine(CultureInfo.InvariantCulture, $"Tool exit code: {process.ExitCode}");
      if (!string.IsNullOrWhiteSpace(process.StandardOutput)) text.AppendLine(process.StandardOutput);
      if (!string.IsNullOrWhiteSpace(process.StandardError)) text.AppendLine(process.StandardError);
    }
    return text.ToString().TrimEnd();
  }
}
