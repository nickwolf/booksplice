using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using BookSplice.Core.Analysis;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.History;

public sealed class ConversionHistoryReader(string directory, int maxRecords = ConversionHistoryReader.DefaultMaxRecords, Func<string, bool>? fileExists = null)
{
  public const int DefaultMaxRecords = 2000;
  private const long MaxRecordBytes = 32L * 1024 * 1024;
  private static readonly TimeSpan PartialGrace = TimeSpan.FromMinutes(1);
  private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;

  public string Folder { get; } = Path.GetFullPath(directory);

  // Newest records first. Blocking I/O: callers run this off the UI thread.
  public ConversionHistoryLoad Read(CancellationToken cancellationToken = default)
  {
    var entries = new List<ConversionHistoryEntry>();
    var skipped = new List<SkippedHistoryRecord>();
    if (!Directory.Exists(Folder)) return new(entries, skipped, 0);
    var files = new List<(string Path, DateTime Written, long Length)>();
    try
    {
      foreach (var file in new DirectoryInfo(Folder).EnumerateFiles())
      {
        cancellationToken.ThrowIfCancellationRequested();
        if (file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) files.Add((file.FullName, file.LastWriteTimeUtc, file.Length));
        else if (file.Extension.Equals(".partial", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - file.LastWriteTimeUtc > PartialGrace)
          skipped.Add(new(file.Name, "Incomplete record left by an interrupted write."));
      }
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      skipped.Add(new(Path.GetFileName(Folder), $"The history folder could not be read: {exception.Message}"));
      return new(entries, skipped, 0);
    }
    files.Sort((left, right) => right.Written.CompareTo(left.Written));
    foreach (var file in files.Take(maxRecords))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var name = Path.GetFileName(file.Path);
      if (file.Length > MaxRecordBytes) { skipped.Add(new(name, "The record is too large to load.")); continue; }
      try
      {
        entries.Add(Parse(File.ReadAllBytes(file.Path), name));
      }
      catch (HistoryRecordException exception) { skipped.Add(new(name, exception.Message)); }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
        skipped.Add(new(name, $"The record could not be read: {exception.Message}"));
      }
    }
    entries.Sort((left, right) => right.StartedAt.CompareTo(left.StartedAt));
    return new(entries, skipped, Math.Max(0, files.Count - maxRecords));
  }

  // Deletes only the JSON record files for these entries, never anything they point to.
  public int Delete(IEnumerable<ConversionHistoryEntry> entries)
  {
    var deleted = 0;
    foreach (var entry in entries)
    {
      var path = Path.Combine(Folder, entry.FileName);
      if (!Path.GetFileName(path).Equals(entry.FileName, StringComparison.Ordinal)) continue;
      try { File.Delete(path); deleted++; }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
    return deleted;
  }

  // Removes every record file, including the ones that were skipped or fell past the load cap.
  public int DeleteAll()
  {
    var deleted = 0;
    if (!Directory.Exists(Folder)) return 0;
    try
    {
      foreach (var file in new DirectoryInfo(Folder).EnumerateFiles().ToArray())
      {
        var isRecord = file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(Path.GetFileNameWithoutExtension(file.Name), out _);
        var isStalePartial = file.Extension.Equals(".partial", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - file.LastWriteTimeUtc > PartialGrace;
        if (!isRecord && !isStalePartial) continue;
        try { file.Delete(); deleted++; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
      }
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    return deleted;
  }

  internal ConversionHistoryEntry Parse(byte[] bytes, string fileName)
  {
    try
    {
      using var document = JsonDocument.Parse(bytes);
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object) throw new HistoryRecordException("The record is not a JSON object.");
      var version = Prop(root, "schemaVersion") is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var parsed) ? parsed : throw new HistoryRecordException("The record has no schema version.");
      if (version > ConversionAuditRecord.CurrentSchemaVersion) throw new HistoryRecordException($"The record uses schema version {version}, which this version of BookSplice does not understand.");
      if (version < 1) throw new HistoryRecordException("The record has an invalid schema version.");
      var jobId = Guid.TryParse(Text(root, "jobId"), out var id) ? id : throw new HistoryRecordException("The record has no valid job id.");
      var started = Time(root, "startedAt");
      var finished = Time(root, "finishedAt");
      if (!Enum.TryParse<ConversionTerminalStatus>(Text(root, "terminalStatus"), out var status) || !Enum.IsDefined(status)) throw new HistoryRecordException("The record has no valid outcome.");
      return Build(root, fileName, jobId, started, finished, status);
    }
    catch (JsonException exception) { throw new HistoryRecordException("The record is not valid JSON: " + exception.Message, exception); }
    catch (Exception exception) when (exception is InvalidOperationException or FormatException) { throw new HistoryRecordException("The record has unexpected contents.", exception); }
  }

  private ConversionHistoryEntry Build(JsonElement root, string fileName, Guid jobId, DateTimeOffset started, DateTimeOffset finished, ConversionTerminalStatus status)
  {
    var diagnostics = Items(root, "diagnostics").ToArray();
    var validation = Prop(root, "validation");
    var warnings = diagnostics.Where(value => Text(value, "severity") == "Warning").Select(value => Text(value, "message"))
      .Concat(Strings(validation, "warnings")).Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).Select(value => value!).ToArray();
    var published = Text(root, "publishedPath") ?? (Text(Prop(root, "publication"), "status") == "Published" ? Text(Prop(root, "publication"), "finalPath") : null);
    var plan = Prop(root, "plan");
    var outcome = status switch
    {
      ConversionTerminalStatus.Succeeded => HistoryOutcome.Completed,
      ConversionTerminalStatus.Cancelled => HistoryOutcome.Cancelled,
      ConversionTerminalStatus.DryRun => HistoryOutcome.Preview,
      _ => HistoryOutcome.Failed,
    };
    var reason = diagnostics.Where(value => Text(value, "severity") == "Error").Select(value => Text(value, "message")).FirstOrDefault()
      ?? Strings(validation, "errors").FirstOrDefault() ?? ConversionResultDisplay.DescribeStatus(status);
    var summary = outcome switch
    {
      HistoryOutcome.Completed => "",
      HistoryOutcome.Cancelled => "The job stopped before completion.",
      HistoryOutcome.Preview => "Nothing was converted.",
      _ => reason,
    };
    var exists = published is not null && _fileExists(published);
    var elapsed = finished >= started ? finished - started : TimeSpan.Zero;
    var strategy = Text(plan, "strategy") ?? "Not recorded";
    var level = Text(plan, "validationLevel") ?? "Not recorded";
    return new(jobId, fileName, started, elapsed, outcome, warnings.Length > 0, summary, strategy, level, Array.AsReadOnly(warnings), published, exists);
  }

  // Re-reads one record for the copy action so the list does not hold every record's tool output in memory.
  public string ReadDiagnostics(ConversionHistoryEntry entry)
  {
    using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Folder, entry.FileName)));
    var root = document.RootElement;
    var plan = Prop(root, "plan");
    var execution = Prop(root, "execution");
    var paths = new List<string>();
    foreach (var path in new[] { entry.OutputPath, Text(plan, "outputPath"), Text(execution, "temporaryOutputPath") })
    {
      if (string.IsNullOrWhiteSpace(path)) continue;
      paths.Add(path);
      if (Path.GetDirectoryName(path) is { Length: > 0 } folder) paths.Add(folder);
    }
    return PublicTextRedactor.Sanitize(BuildDiagnostics(root, entry), paths);
  }

  private static string BuildDiagnostics(JsonElement root, ConversionHistoryEntry entry)
  {
    var (jobId, started) = (entry.JobId, entry.StartedAt);
    var finished = started + entry.Elapsed;
    var warnings = entry.Warnings;
    var status = Enum.TryParse<ConversionTerminalStatus>(Text(root, "terminalStatus"), out var parsed) ? parsed : ConversionTerminalStatus.UnexpectedFailure;
    var text = new StringBuilder().AppendLine("BookSplice conversion record");
    text.Append("Job: ").AppendLine(jobId.ToString("D"));
    text.Append("Status: ").AppendLine(status.ToString());
    text.Append("Started: ").AppendLine(started.ToString("O", CultureInfo.InvariantCulture));
    text.Append("Finished: ").AppendLine(finished.ToString("O", CultureInfo.InvariantCulture));
    text.Append("Output: ").AppendLine(entry.OutputPath is null ? "none" : entry.OutputExists ? "published" : "published, file missing");
    var plan = Prop(root, "plan");
    foreach (var (label, key) in new[] { ("Strategy", "strategy"), ("Quality", "qualityProfile"), ("Validation level", "validationLevel"), ("Metadata profile", "metadataProfileId") })
      if (Text(plan, key) is { } value) text.Append(label).Append(": ").AppendLine(value);
    foreach (var warning in warnings) text.Append("Warning: ").AppendLine(warning);
    foreach (var diagnostic in Items(root, "diagnostics")) text.Append(Text(diagnostic, "severity")).Append(" [").Append(Text(diagnostic, "code")).Append("]: ").AppendLine(Text(diagnostic, "message"));
    var execution = Prop(root, "execution");
    foreach (var diagnostic in Items(execution, "diagnostics")) text.Append("Execution [").Append(Text(diagnostic, "code")).Append("]: ").AppendLine(Text(diagnostic, "message"));
    foreach (var error in Strings(Prop(root, "validation"), "errors")) text.Append("Validation error: ").AppendLine(error);
    foreach (var diagnostic in Strings(Prop(root, "publication"), "diagnostics")) text.Append("Publication: ").AppendLine(diagnostic);
    foreach (var process in Items(execution, "processes"))
    {
      text.Append("Tool exit code: ").AppendLine(Prop(process, "exitCode") is { ValueKind: JsonValueKind.Number } code ? code.GetRawText() : "unknown");
      if (Text(process, "standardError") is { Length: > 0 } error) text.AppendLine(error);
    }
    return text.ToString().TrimEnd();
  }

  private static JsonElement Prop(JsonElement? element, string name)
    => element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var found) ? found : default;

  private static string? Text(JsonElement? element, string name) => Prop(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

  private static IEnumerable<JsonElement> Items(JsonElement? element, string name)
    => Prop(element, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object) : [];

  private static IEnumerable<string> Strings(JsonElement? element, string name)
    => Prop(element, name) is { ValueKind: JsonValueKind.Array } array
      ? array.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!) : [];

  private static DateTimeOffset Time(JsonElement root, string name)
    => DateTimeOffset.TryParse(Text(root, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : throw new HistoryRecordException($"The record has no valid {name}.");
}

public sealed class HistoryRecordException : Exception
{
  public HistoryRecordException(string message) : base(message) { }
  public HistoryRecordException(string message, Exception inner) : base(message, inner) { }
}
