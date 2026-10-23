namespace BookSplice.Gui.Diagnostics;

public enum CrashSource { Dispatcher, AppDomain, Startup }

public enum CrashLaunchMode { Normal, UpdateCandidate }

public sealed class CrashExceptionInfo
{
  public int Depth { get; init; }
  public string Type { get; init; } = "";
  public string Message { get; init; } = "";
  public string StackTrace { get; init; } = "";
}

public sealed class CrashReport
{
  public const int CurrentSchemaVersion = 1;
  public const int MaxExceptionDepth = 5;
  private const int MaxInnerExceptions = 20;

  public int SchemaVersion { get; init; } = CurrentSchemaVersion;
  public DateTimeOffset CreatedUtc { get; init; }
  public string AppVersion { get; init; } = "unknown";
  public string LaunchMode { get; init; } = "normal";
  public string Source { get; init; } = "";
  public List<string> JobIds { get; init; } = [];
  public string ExceptionType { get; init; } = "";
  public string Message { get; init; } = "";
  public string StackTrace { get; init; } = "";
  public List<CrashExceptionInfo> InnerExceptions { get; init; } = [];
  public bool Truncated { get; init; }

  public static CrashReport Create(Exception exception, CrashSource source, CrashLaunchMode mode,
    string? appVersion, IEnumerable<Guid> jobIds, DateTimeOffset now)
  {
    ArgumentNullException.ThrowIfNull(exception);
    var inner = new List<CrashExceptionInfo>();
    var omitted = Collect(exception, 1, inner);
    return new CrashReport
    {
      CreatedUtc = now.ToUniversalTime(),
      AppVersion = string.IsNullOrWhiteSpace(appVersion) ? "unknown" : appVersion,
      LaunchMode = mode == CrashLaunchMode.UpdateCandidate ? "update-candidate" : "normal",
      Source = source switch { CrashSource.Dispatcher => "dispatcher", CrashSource.AppDomain => "appdomain", _ => "startup" },
      JobIds = jobIds.Select(id => id.ToString("D")).ToList(),
      ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
      Message = exception.Message,
      StackTrace = exception.StackTrace ?? "",
      InnerExceptions = inner,
      Truncated = omitted
    };
  }

  // Returns true when exceptions were left out because of the depth or count bound.
  private static bool Collect(Exception exception, int depth, List<CrashExceptionInfo> into)
  {
    var children = exception is AggregateException aggregate
      ? aggregate.InnerExceptions.ToList()
      : exception.InnerException is { } single ? [single] : new List<Exception>();
    var omitted = false;
    foreach (var child in children)
    {
      if (depth >= MaxExceptionDepth || into.Count >= MaxInnerExceptions) { omitted = true; continue; }
      into.Add(new CrashExceptionInfo
      {
        Depth = depth,
        Type = child.GetType().FullName ?? child.GetType().Name,
        Message = child.Message,
        StackTrace = child.StackTrace ?? ""
      });
      omitted |= Collect(child, depth + 1, into);
    }
    return omitted;
  }
}
