using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BookSplice.Core.Settings;

namespace BookSplice.Core.Queue;

public enum QueueLoadCode { Missing, Loaded, Corrupt, FutureVersion, Unreadable }

public sealed record QueueLoadResult(QueueLoadCode Code, QueueSnapshot? Snapshot, string? Detail = null, string? PreservedPath = null);

public interface IQueueSnapshotStore
{
  string Path { get; }
  QueueLoadResult Load();
  /// <summary>Returns false without writing when the existing file must be preserved.</summary>
  bool Save(QueueSnapshot snapshot);
}

public enum QueueVersionCheck { Current, Future, Invalid }

public static class QueueSnapshotMigration
{
  /// <summary>Version 1 is the only format. A missing or malformed version is invalid, never guessed.</summary>
  public static QueueVersionCheck Check(JsonObject root)
  {
    if (root["schemaVersion"] is not JsonValue value || !value.TryGetValue<int>(out var version) || version < 1) return QueueVersionCheck.Invalid;
    return version > QueueSnapshot.CurrentSchemaVersion ? QueueVersionCheck.Future : QueueVersionCheck.Current;
  }
}

public sealed class QueueSnapshotStore(string dataDirectory) : IQueueSnapshotStore
{
  public const string FileName = "queue.json";
  public const int MaximumItems = 10_000;
  private const long MaximumBytes = 32 * 1024 * 1024;

  private static readonly JsonSerializerOptions Options = new()
  {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    MaxDepth = 32,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
  };

  public string Path { get; } = System.IO.Path.Combine(dataDirectory, FileName);

  /// <summary>Set when the file on disk is not ours to replace: a newer version, or an unreadable one that could not be moved aside.</summary>
  public bool IsWriteBlocked { get; private set; }

  public QueueLoadResult Load()
  {
    AtomicFile.DeleteLeftovers(dataDirectory, FileName);
    byte[] bytes;
    try
    {
      if (!File.Exists(Path)) return new(QueueLoadCode.Missing, null);
      if (new FileInfo(Path).Length > MaximumBytes) return Reject("the file is too large");
      bytes = File.ReadAllBytes(Path);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      IsWriteBlocked = true;
      return new(QueueLoadCode.Unreadable, null, exception.Message);
    }
    var (code, snapshot, detail) = Parse(bytes);
    return code switch
    {
      QueueLoadCode.Loaded => new(code, snapshot),
      QueueLoadCode.FutureVersion => Preserve(detail),
      _ => Reject(detail),
    };
  }

  public bool Save(QueueSnapshot snapshot)
  {
    ArgumentNullException.ThrowIfNull(snapshot);
    if (IsWriteBlocked) return false;
    var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Options);
    AtomicFile.Write(Path, bytes);
    return true;
  }

  public static (QueueLoadCode Code, QueueSnapshot? Snapshot, string? Detail) Parse(byte[] bytes)
  {
    try
    {
      if (JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) is not JsonObject root) return (QueueLoadCode.Corrupt, null, "the root must be an object");
      switch (QueueSnapshotMigration.Check(root))
      {
        case QueueVersionCheck.Invalid: return (QueueLoadCode.Corrupt, null, "the schema version is missing or invalid");
        case QueueVersionCheck.Future: return (QueueLoadCode.FutureVersion, null, "the schema version is newer than this version supports");
      }
      var snapshot = JsonSerializer.Deserialize<QueueSnapshot>(bytes, Options);
      return Validate(snapshot) is { } problem ? (QueueLoadCode.Corrupt, null, problem) : (QueueLoadCode.Loaded, snapshot, null);
    }
    catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
    { return (QueueLoadCode.Corrupt, null, exception.Message); }
  }

  private static string? Validate(QueueSnapshot? snapshot)
  {
    if (snapshot?.Items is null) return "the item list is missing";
    if (snapshot.Items.Count > MaximumItems) return "the item list is too long";
    foreach (var item in snapshot.Items)
    {
      if (item is null) return "an item is empty";
      if (string.IsNullOrWhiteSpace(item.Source) || item.Source.Contains('\0')) return "an item has no source";
      if (item.Settings is null) return "an item has no settings";
      if (!Enum.IsDefined(item.Settings.ChannelPolicy) || !Enum.IsDefined(item.Settings.CollisionPolicy) || !Enum.IsDefined(item.Settings.ValidationLevel)) return "an item has an unknown setting value";
      if (item.Settings.OutputDirectory.Contains('\0')) return "an item has an invalid output folder";
      if (!Enum.IsDefined(item.State)) return "an item has an unknown state";
      if (item.Selection is { } selection && (selection.Paths is null || selection.Paths.Count == 0 || string.IsNullOrWhiteSpace(selection.RootPath) || selection.Paths.Any(string.IsNullOrWhiteSpace))) return "an item has an invalid selection";
      if (item.ReviewedOrder is { } order && (order.Included is null || order.Excluded is null)) return "an item has an invalid track order";
      if (item.ExpectedSources?.Any(identity => identity is null || string.IsNullOrWhiteSpace(identity.Path)) == true) return "an item has an invalid source identity";
      if (item.Cover is { } cover && (string.IsNullOrWhiteSpace(cover.SourcePath) || string.IsNullOrEmpty(cover.ContentHash))) return "an item has an invalid cover";
    }
    return null;
  }

  private QueueLoadResult Preserve(string? detail)
  {
    IsWriteBlocked = true;
    return new(QueueLoadCode.FutureVersion, null, detail, Path);
  }

  // The unreadable file is moved aside so it survives; if that fails it is left in place and never overwritten.
  private QueueLoadResult Reject(string? detail)
  {
    var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    for (var attempt = 0; attempt < 100; attempt++)
    {
      var target = $"{Path}.corrupt-{stamp}{(attempt == 0 ? "" : "-" + attempt.ToString(CultureInfo.InvariantCulture))}";
      if (File.Exists(target)) continue;
      try
      {
        File.Move(Path, target);
        return new(QueueLoadCode.Corrupt, null, detail, target);
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { break; }
    }
    IsWriteBlocked = true;
    return new(QueueLoadCode.Corrupt, null, detail, Path);
  }
}
