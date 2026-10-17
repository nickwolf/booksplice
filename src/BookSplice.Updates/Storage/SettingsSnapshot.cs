using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Storage;

[SupportedOSPlatform("windows")]
public sealed class SettingsSnapshot
{
  public const int MaximumBytes = 1024 * 1024;
  private readonly byte[]? _bytes;
  private static readonly UTF8Encoding StrictUtf8 = new(false, true);

  private SettingsSnapshot(byte[]? bytes, int? schemaVersion, string parentPath,
    FileIdentity parentIdentity)
  {
    _bytes = bytes?.ToArray();
    SchemaVersion = schemaVersion;
    Sha256 = bytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(bytes));
    ParentPath = parentPath;
    ParentIdentity = parentIdentity;
  }

  public bool IsAbsent => _bytes is null;
  public int? SchemaVersion { get; }
  public string? Sha256 { get; }
  public byte[]? Bytes => _bytes?.ToArray();
  public string ParentPath { get; }
  public FileIdentity ParentIdentity { get; }

  public static Task<SettingsSnapshot> CaptureAsync(string settingsPath,
    CancellationToken cancellationToken = default)
    => CaptureAsync(settingsPath, null, cancellationToken);

  internal static Task<SettingsSnapshot> CaptureAsync(string settingsPath,
    Action? afterFileOpen, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (!RecoveryRecordValidation.Absolute(settingsPath) ||
        !string.Equals(Path.GetFileName(settingsPath), "settings.json",
          StringComparison.OrdinalIgnoreCase))
      throw new ArgumentException("The settings path must be canonical.", nameof(settingsPath));
    using var parent = SafeDirectoryHandle.OpenPath(Path.GetDirectoryName(settingsPath)!, false);
    try
    {
      using var handle = parent.OpenFile(Path.GetFileName(settingsPath));
      SafeDirectoryHandle.RejectReparseFile(handle);
      afterFileOpen?.Invoke();
      using var stream = new FileStream(handle, FileAccess.Read);
      if (stream.Length > MaximumBytes)
        throw new InvalidDataException("Settings exceed the updater snapshot limit.");
      var bytes = new byte[checked((int)stream.Length)];
      stream.ReadExactly(bytes);
      cancellationToken.ThrowIfCancellationRequested();
      _ = StrictUtf8.GetString(bytes);
      using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
      if (document.RootElement.ValueKind != JsonValueKind.Object ||
          HasDuplicates(document.RootElement) ||
          !document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
          schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) ||
          version != 1)
        throw new InvalidDataException("Settings schema is incompatible with updater protocol 1.");
      return Task.FromResult(new SettingsSnapshot(bytes, version, parent.Path, parent.Identity));
    }
    catch (Win32Exception error) when (error.NativeErrorCode == 2)
    {
      return Task.FromResult(new SettingsSnapshot(null, null, parent.Path, parent.Identity));
    }
  }

  public async Task VerifyUnchangedAsync(string settingsPath,
    CancellationToken cancellationToken = default)
  {
    if (!string.Equals(Path.GetDirectoryName(settingsPath), ParentPath,
      StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("The settings source path changed.");
    var current = await CaptureAsync(settingsPath, cancellationToken).ConfigureAwait(false);
    if (current.ParentIdentity != ParentIdentity || current.IsAbsent != IsAbsent ||
        current.Sha256 != Sha256)
      throw new InvalidDataException("Settings changed during update handover.");
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
      foreach (var item in element.EnumerateArray())
        if (HasDuplicates(item)) return true;
    return false;
  }
}
