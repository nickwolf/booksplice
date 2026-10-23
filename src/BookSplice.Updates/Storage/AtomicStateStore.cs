using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Storage;

public enum StateReadKind { Missing, Valid, Invalid }

public sealed record StateRead<T>(StateReadKind Kind, T? Value = default, string? Error = null)
{ }

public interface IStateRecordDefinition
{
  string Name { get; }
  Type RecordType { get; }
  int MaximumBytes { get; }
  bool Validate(object value);
}

public sealed class StateRecordDefinition<T>(string name, Func<T, bool> validator,
  int maximumBytes = 1024 * 1024) : IStateRecordDefinition
  where T : class
{
  public string Name { get; } = name;
  public Type RecordType => typeof(T);
  public int MaximumBytes { get; } = maximumBytes;
  public bool Validate(object value) => value is T typed && validator(typed);
}

internal enum StateWritePoint { TempCreated, BytesWritten, Flushed, BeforeReplace, Replaced }

[SupportedOSPlatform("windows")]
public sealed class AtomicStateStore
{
  private readonly string _rootPath;
  private readonly FileIdentity _rootIdentity;
  private readonly Dictionary<string, IStateRecordDefinition> _definitions;
  private readonly Action<StateWritePoint>? _fault;
  private readonly Action? _afterRootOpen;
  private static readonly UTF8Encoding StrictUtf8 = new(false, true);
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    PropertyNameCaseInsensitive = false,
    NumberHandling = JsonNumberHandling.Strict,
    RespectRequiredConstructorParameters = true,
    MaxDepth = 32
  };

  public AtomicStateStore(OwnedDirectory recordRoot, IEnumerable<IStateRecordDefinition> definitions)
    : this(recordRoot, definitions, null) { }

  internal AtomicStateStore(OwnedDirectory recordRoot,
    IEnumerable<IStateRecordDefinition> definitions, Action<StateWritePoint>? fault,
    Action? afterRootOpen = null)
  {
    ArgumentNullException.ThrowIfNull(recordRoot);
    ArgumentNullException.ThrowIfNull(definitions);
    if (!recordRoot.Identity.IsValid || recordRoot.InstallationId == Guid.Empty)
      throw new ArgumentException("The state root identity is invalid.", nameof(recordRoot));
    _rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recordRoot.Path));
    if (!string.Equals(_rootPath, recordRoot.Path, StringComparison.OrdinalIgnoreCase))
      throw new ArgumentException("The state root path must be canonical.", nameof(recordRoot));
    _rootIdentity = recordRoot.Identity;
    _definitions = new(StringComparer.OrdinalIgnoreCase);
    foreach (var definition in definitions)
    {
      if (!SafeZipExtractor.IsSafeComponent(definition.Name) ||
          !definition.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
          definition.MaximumBytes is < 1 or > 16 * 1024 * 1024 ||
          !_definitions.TryAdd(definition.Name, definition))
        throw new ArgumentException("State filenames must be unique JSON components.", nameof(definitions));
    }
    if (_definitions.Count == 0)
      throw new ArgumentException("At least one state record is required.", nameof(definitions));
    _fault = fault;
    _afterRootOpen = afterRootOpen;
    using var root = OpenRoot();
  }

  public StateRead<T> Read<T>(string path) where T : class
  {
    var definition = Definition<T>(path);
    using var root = OpenRoot();
    _afterRootOpen?.Invoke();
    using var stateLock = Lock(root, definition.Name);
    return ReadLocked<T>(root, definition);
  }

  public void Write<T>(string path, T value) where T : class
  {
    var definition = Definition<T>(path);
    using var root = OpenRoot();
    _afterRootOpen?.Invoke();
    using var stateLock = Lock(root, definition.Name);
    WriteLocked(root, definition, value);
  }

  public T Transition<T>(string path, Func<StateRead<T>, T> transition) where T : class
  {
    ArgumentNullException.ThrowIfNull(transition);
    var definition = Definition<T>(path);
    using var root = OpenRoot();
    _afterRootOpen?.Invoke();
    using var stateLock = Lock(root, definition.Name);
    var current = ReadLocked<T>(root, definition);
    var next = transition(current);
    WriteLocked(root, definition, next);
    return next;
  }

  private SafeDirectoryHandle OpenRoot()
  {
    var root = SafeDirectoryHandle.OpenPath(_rootPath, false, _rootPath, _rootIdentity,
      allowEntryMutation: true);
    if (root.Identity != _rootIdentity)
    {
      root.Dispose();
      throw new IOException("The state root has changed.");
    }
    return root;
  }

  private IStateRecordDefinition Definition<T>(string path) where T : class
  {
    ArgumentNullException.ThrowIfNull(path);
    var full = Path.GetFullPath(path);
    if (!string.Equals(full, path, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(Path.GetDirectoryName(full), _rootPath, StringComparison.OrdinalIgnoreCase) ||
        !_definitions.TryGetValue(Path.GetFileName(full), out var definition) ||
        definition.RecordType != typeof(T))
      throw new ArgumentException("The state path or record type is not registered.", nameof(path));
    return definition;
  }

  private static FileStream Lock(SafeDirectoryHandle root, string name)
  {
    var stream = new FileStream(root.OpenOrCreateFile(name + ".lock"), FileAccess.ReadWrite);
    try
    {
      var timer = Stopwatch.StartNew();
      while (true)
      {
        try
        {
          stream.Lock(0, 1);
          return stream;
        }
        catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
          Thread.Sleep(10);
        }
      }
    }
    catch { stream.Dispose(); throw; }
  }

  private static StateRead<T> ReadLocked<T>(SafeDirectoryHandle root,
    IStateRecordDefinition definition) where T : class
  {
    try
    {
      using var handle = root.OpenStateFile(definition.Name);
      SafeDirectoryHandle.RejectReparseFile(handle);
      using var stream = new FileStream(handle, FileAccess.Read);
      if (stream.Length > definition.MaximumBytes)
        return new StateRead<T>(StateReadKind.Invalid, Error: "The state record exceeds its byte limit.");
      var bytes = new byte[checked((int)stream.Length)];
      stream.ReadExactly(bytes);
      var json = StrictUtf8.GetString(bytes);
      using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
      if (document.RootElement.ValueKind != JsonValueKind.Object ||
          !document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
          schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) ||
          version != 1 || HasDuplicates(document.RootElement))
        return new StateRead<T>(StateReadKind.Invalid, Error: "The state schema or property set is invalid.");
      var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
      return value is not null && definition.Validate(value)
        ? new StateRead<T>(StateReadKind.Valid, value)
        : new StateRead<T>(StateReadKind.Invalid, Error: "The state record failed validation.");
    }
    catch (Win32Exception error) when (error.NativeErrorCode == 2)
    {
      return new StateRead<T>(StateReadKind.Missing);
    }
    catch (Exception error) when (error is IOException or Win32Exception or JsonException or
      DecoderFallbackException or NotSupportedException or UnauthorizedAccessException)
    {
      return new StateRead<T>(StateReadKind.Invalid, Error: error.Message);
    }
  }

  private static bool HasDuplicates(JsonElement element)
  {
    if (element.ValueKind == JsonValueKind.Object)
    {
      var names = new HashSet<string>(StringComparer.Ordinal);
      foreach (var property in element.EnumerateObject())
        if (!names.Add(property.Name) || HasDuplicates(property.Value)) return true;
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
      foreach (var item in element.EnumerateArray())
        if (HasDuplicates(item)) return true;
    }
    return false;
  }

  private void WriteLocked<T>(SafeDirectoryHandle root, IStateRecordDefinition definition,
    T value) where T : class
  {
    ArgumentNullException.ThrowIfNull(value);
    if (!definition.Validate(value))
      throw new InvalidDataException("The state record failed validation.");
    var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
    if (bytes.Length > definition.MaximumBytes)
      throw new InvalidDataException("The state record exceeds its byte limit.");
    using (var document = JsonDocument.Parse(bytes))
    {
      if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
          schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) ||
          version != 1)
        throw new InvalidDataException("The state schema is invalid.");
    }
    var tempName = definition.Name + "." + Guid.NewGuid().ToString("N") + ".tmp";
    using var temp = root.CreateStateTemp(tempName);
    _fault?.Invoke(StateWritePoint.TempCreated);
    using var stream = new FileStream(temp, FileAccess.Write);
    stream.Write(bytes);
    _fault?.Invoke(StateWritePoint.BytesWritten);
    stream.Flush(true);
    _fault?.Invoke(StateWritePoint.Flushed);
    _fault?.Invoke(StateWritePoint.BeforeReplace);
    SafeDirectoryHandle.RenameStateTemp(temp, root, definition.Name);
    _fault?.Invoke(StateWritePoint.Replaced);
  }
}
