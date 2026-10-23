using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BookSplice.Gui.Diagnostics;

public interface ICrashReportSink
{
  bool TryWrite(CrashReport report);
}

public interface ICrashReportFiles
{
  void CreateDirectory(string path);
  void WriteNew(string path, byte[] bytes);
  void Move(string source, string destination);
  void Delete(string path);
  bool Exists(string path);
  IReadOnlyList<string> List(string directory, string pattern);
  byte[] Read(string path, int maxBytes);
}

public sealed class CrashReportFiles : ICrashReportFiles
{
  public void CreateDirectory(string path) => Directory.CreateDirectory(path);

  public void WriteNew(string path, byte[] bytes)
  {
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
    stream.Write(bytes);
    stream.Flush(true);
  }

  public void Move(string source, string destination) => File.Move(source, destination, overwrite: false);

  public void Delete(string path) => File.Delete(path);

  public bool Exists(string path) => File.Exists(path);

  public IReadOnlyList<string> List(string directory, string pattern) =>
    Directory.Exists(directory) ? Directory.GetFiles(directory, pattern) : [];

  public byte[] Read(string path, int maxBytes)
  {
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    if (stream.Length > maxBytes) throw new InvalidDataException("Crash report is too large.");
    var bytes = new byte[stream.Length];
    stream.ReadExactly(bytes);
    return bytes;
  }
}

public sealed record CrashReportEntry(string Path, CrashReport Report);

// Reports are one JSON file each under <data root>\crash-reports. Nothing here reads or sends them anywhere else.
public sealed class CrashReportStore(string directory, ICrashReportFiles? files = null, TimeProvider? clock = null) : ICrashReportSink
{
  public const int MaxReportBytes = 64 * 1024;
  public const int RetainedReports = 5;
  public const string DirectoryName = "crash-reports";
  private const string ReviewedSuffix = ".reviewed";
  private const string TruncationMarker = "...[truncated]";
  private readonly ICrashReportFiles _files = files ?? new CrashReportFiles();
  private readonly TimeProvider _clock = clock ?? TimeProvider.System;

  private static readonly JsonSerializerOptions Json = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
  };

  public static string DirectoryFor(string dataRoot) => System.IO.Path.Combine(dataRoot, DirectoryName);

  public bool TryWrite(CrashReport report)
  {
    try
    {
      var bytes = Serialize(report);
      _files.CreateDirectory(directory);
      Quietly(Prune);
      if (_files.List(directory, "crash-*.json").Count >= RetainedReports) return false;
      var stamp = _clock.GetUtcNow().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
      var name = $"crash-{stamp}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}.json";
      var final = System.IO.Path.Combine(directory, name);
      var temp = final + ".tmp";
      try
      {
        _files.WriteNew(temp, bytes);
        _files.Move(temp, final);
      }
      catch
      {
        Quietly(() => _files.Delete(temp));
        return false;
      }
      return true;
    }
    catch { return false; }
  }

  public IReadOnlyList<CrashReportEntry> ListUnreviewed()
  {
    var found = new List<CrashReportEntry>();
    try
    {
      foreach (var path in _files.List(directory, "crash-*.json").OrderByDescending(System.IO.Path.GetFileName, StringComparer.Ordinal))
      {
        try
        {
          if (_files.Exists(path + ReviewedSuffix)) continue;
          var report = JsonSerializer.Deserialize<CrashReport>(_files.Read(path, MaxReportBytes * 2), Json);
          if (report is { SchemaVersion: CrashReport.CurrentSchemaVersion }) found.Add(new CrashReportEntry(path, report));
        }
        catch { }
      }
    }
    catch { return []; }
    return found;
  }

  public void MarkReviewed(IEnumerable<CrashReportEntry> entries)
  {
    foreach (var entry in entries)
      Quietly(() => { if (!_files.Exists(entry.Path + ReviewedSuffix)) _files.WriteNew(entry.Path + ReviewedSuffix, []); });
  }

  private void Prune()
  {
    var reports = _files.List(directory, "crash-*.json").OrderByDescending(System.IO.Path.GetFileName, StringComparer.Ordinal).ToList();
    foreach (var old in reports.Skip(RetainedReports - 1))
    {
      Quietly(() => _files.Delete(old));
      Quietly(() => { if (_files.Exists(old + ReviewedSuffix)) _files.Delete(old + ReviewedSuffix); });
    }
    foreach (var leftover in _files.List(directory, "crash-*.json.tmp")) Quietly(() => _files.Delete(leftover));
  }

  internal static byte[] Serialize(CrashReport report)
  {
    (int Message, int Stack, int Inner)[] levels = [(4000, 12000, 20), (1000, 4000, 10), (300, 1500, 5), (100, 400, 0), (0, 0, 0)];
    foreach (var (message, stack, inner) in levels)
    {
      var bytes = JsonSerializer.SerializeToUtf8Bytes(Fit(report, message, stack, inner), Json);
      if (bytes.Length <= MaxReportBytes) return bytes;
    }
    throw new InvalidOperationException("Crash report does not fit the size limit.");
  }

  private static CrashReport Fit(CrashReport report, int messageLimit, int stackLimit, int innerLimit)
  {
    var truncated = report.Truncated;
    string Cut(string value, int limit)
    {
      if (value.Length <= limit) return value;
      truncated = true;
      var keep = limit > 0 && char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit;
      return value[..keep] + TruncationMarker;
    }
    var inner = report.InnerExceptions.Take(innerLimit).Select(item => new CrashExceptionInfo
    {
      Depth = item.Depth,
      Type = Cut(item.Type, 300),
      Message = Cut(item.Message, messageLimit),
      StackTrace = Cut(item.StackTrace, stackLimit)
    }).ToList();
    if (inner.Count < report.InnerExceptions.Count) truncated = true;
    return new CrashReport
    {
      SchemaVersion = report.SchemaVersion,
      CreatedUtc = report.CreatedUtc,
      AppVersion = Cut(report.AppVersion, 64),
      LaunchMode = report.LaunchMode,
      Source = report.Source,
      JobIds = report.JobIds.Take(64).ToList(),
      ExceptionType = Cut(report.ExceptionType, 300),
      Message = Cut(report.Message, messageLimit),
      StackTrace = Cut(report.StackTrace, stackLimit),
      InnerExceptions = inner,
      Truncated = truncated || report.JobIds.Count > 64
    };
  }

  private static void Quietly(Action action)
  {
    try { action(); } catch { }
  }
}
