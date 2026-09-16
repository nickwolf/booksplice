using System.Text.Json;
using AudiobookConverter.Core.Analysis;

namespace AudiobookConverter.FFmpeg.Execution;

internal interface IJobLogFileOperations
{
  Stream Create(string path);
  bool Exists(string path);
  void Replace(string source, string destination);
  void Move(string source, string destination);
  void Delete(string path);
}

internal sealed class JobLogFileOperations : IJobLogFileOperations
{
  public Stream Create(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
  public bool Exists(string path) => File.Exists(path);
  public void Replace(string source, string destination) => File.Replace(source, destination, null);
  public void Move(string source, string destination) => File.Move(source, destination);
  public void Delete(string path) => File.Delete(path);
}

public sealed partial class JsonJobLogWriter : IJobLogWriter
{
  private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
  private readonly string _directory;
  private readonly IJobLogFileOperations _files;

  public JsonJobLogWriter(string directory) : this(directory, new JobLogFileOperations()) { }

  internal JsonJobLogWriter(string directory, IJobLogFileOperations files)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);
    _directory = Path.GetFullPath(directory);
    _files = files ?? throw new ArgumentNullException(nameof(files));
  }

  public async Task WriteAsync(ConversionAuditRecord record, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(record);
    Directory.CreateDirectory(_directory);
    var destination = Path.Combine(_directory, $"{record.JobId:D}.json");
    var partial = Path.Combine(_directory, $".{record.JobId:D}.{Guid.NewGuid():N}.partial");
    try
    {
      await using (var stream = _files.Create(partial))
      {
        await JsonSerializer.SerializeAsync(stream, CreateDocument(record), SerializerOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (stream is FileStream fileStream) fileStream.Flush(flushToDisk: true);
      }
      cancellationToken.ThrowIfCancellationRequested();
      if (_files.Exists(destination)) _files.Replace(partial, destination);
      else _files.Move(partial, destination);
    }
    finally
    {
      try { if (_files.Exists(partial)) _files.Delete(partial); } catch { }
    }
  }

  private static object CreateDocument(ConversionAuditRecord record)
  {
    var plan = record.Planning?.Plan;
    return new
    {
      record.SchemaVersion,
      record.JobId,
      record.StartedAt,
      record.FinishedAt,
      TerminalStatus = record.TerminalStatus.ToString(),
      OrderedSources = record.OrderedSources,
      record.ImportedMetadata,
      record.FinalMetadata,
      SelectedCover = record.SelectedCoverHash is null ? null : new { Hash = record.SelectedCoverHash, Origin = record.SelectedCoverOrigin },
      Analysis = record.Analysis is null ? null : new
      {
        Status = record.Analysis.Status.ToString(),
        record.Analysis.SourceRootName,
        Diagnostics = record.Analysis.Diagnostics.Select(value => new { value.Code, Severity = value.Severity.ToString(), Message = Sanitize(value.Message, record.OrderedSources) }),
      },
      Plan = plan is null ? null : new
      {
        plan.PlanId,
        Strategy = plan.Strategy.ToString(),
        plan.StrategyReasonCodes,
        plan.QualityProfile,
        ValidationLevel = plan.ValidationLevel.ToString(),
        CollisionPolicy = plan.CollisionPolicy.ToString(),
        plan.OutputPath,
        plan.Space,
        plan.ConversionJobs,
        plan.JobsReason,
        plan.MetadataProfileId,
        plan.SampleRate,
        plan.Channels,
      },
      Execution = record.Execution is null ? null : new
      {
        Status = record.Execution.Status.ToString(),
        record.Execution.TemporaryOutputPath,
        Processes = record.Execution.Processes.Select(value => new { value.ExitCode, StandardOutput = Sanitize(value.StandardOutput, record.OrderedSources), StandardError = Sanitize(value.StandardError, record.OrderedSources), value.ChildCpuTime }),
        Diagnostics = record.Execution.Diagnostics.Select(value => new { value.Code, Message = Sanitize(value.Message, record.OrderedSources) }),
      },
      Validation = record.Validation is null ? null : new
      {
        record.Validation.IsValid,
        record.Validation.Checks,
        record.Validation.OutputFacts,
        Warnings = record.Validation.Warnings.Select(value => Sanitize(value, record.OrderedSources)),
        Errors = record.Validation.Errors.Select(value => Sanitize(value, record.OrderedSources)),
      },
      Publication = record.Publication is null ? null : new
      {
        Status = record.Publication.Status.ToString(),
        record.Publication.FinalPath,
        Diagnostics = record.Publication.Diagnostics.Select(value => Sanitize(value, record.OrderedSources)),
      },
      CommandEvidence = record.Execution?.Processes
        .Where(process => !string.IsNullOrWhiteSpace(process.Executable))
        .Select(process => FormatCommand(process, record.OrderedSources)) ?? [],
      record.Timings,
      Diagnostics = record.Diagnostics.Select(value => new { value.Code, Severity = value.Severity.ToString(), Message = Sanitize(value.Message, record.OrderedSources) }),
      record.PublishedPath,
    };
  }

  private static string Sanitize(string value, IReadOnlyList<string> orderedSources)
    => PublicTextRedactor.Sanitize(value, orderedSources);

  private static string FormatCommand(Core.Execution.ExecutionProcessResult process, IReadOnlyList<string> orderedSources)
  {
    var arguments = (process.Arguments ?? []).Select(argument =>
    {
      var sourceIndex = orderedSources.Select((path, index) => (path, index)).FirstOrDefault(item => string.Equals(item.path, argument, StringComparison.OrdinalIgnoreCase));
      return sourceIndex.path is null ? argument : $"[source-{sourceIndex.index + 1}]";
    });
    return Sanitize(string.Join(' ', new[] { process.Executable! }.Concat(arguments).Select(Quote)), orderedSources);
  }

  private static string Quote(string value)
  {
    if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"')) return value;
    var builder = new System.Text.StringBuilder("\"");
    var backslashes = 0;
    foreach (var character in value)
    {
      if (character == '\\') { backslashes++; continue; }
      if (character == '"') builder.Append('\\', backslashes * 2 + 1).Append(character);
      else { builder.Append('\\', backslashes).Append(character); }
      backslashes = 0;
    }
    return builder.Append('\\', backslashes * 2).Append('"').ToString();
  }

}
