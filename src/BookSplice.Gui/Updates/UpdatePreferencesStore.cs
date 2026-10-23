using System.IO;
using System.Text.Json;

namespace BookSplice.Gui.Updates;

public sealed record UpdatePreferences(bool AutomaticChecksEnabled = true, DateTimeOffset? LastSuccess = null, string? NotifiedTag = null);

// Kept apart from AppSettings so update state never changes conversion defaults.
public sealed class UpdatePreferencesStore(string dataRoot)
{
  private readonly string _path = Path.Combine(dataRoot, "updates", "preferences.json");

  public UpdatePreferences Load()
  {
    try
    {
      if (File.Exists(_path) && new FileInfo(_path).Length <= 16 * 1024 &&
          JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllBytes(_path)) is { } value)
        return value;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
    { }
    return new UpdatePreferences();
  }

  public async Task SaveAsync(UpdatePreferences value)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
      await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(value));
      File.Move(temporary, _path, true);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
  }
}
