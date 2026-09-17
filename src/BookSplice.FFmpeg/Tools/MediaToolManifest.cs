using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookSplice.FFmpeg.Tools;

public sealed record MediaToolManifest
{
  [JsonPropertyName("schemaVersion")]
  public required int SchemaVersion { get; init; }

  [JsonPropertyName("provider")]
  public required string Provider { get; init; }

  [JsonPropertyName("release")]
  public required string Release { get; init; }

  [JsonPropertyName("asset")]
  public required string Asset { get; init; }

  [JsonPropertyName("variant")]
  public required string Variant { get; init; }

  [JsonPropertyName("expectedVersionPrefix")]
  public required string ExpectedVersionPrefix { get; init; }

  [JsonPropertyName("sha256")]
  public required string Sha256 { get; init; }

  [JsonPropertyName("expectedExecutables")]
  public required IReadOnlyList<string> ExpectedExecutables { get; init; }

  public static MediaToolManifest Load(string path)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);

    try
    {
      using var stream = File.OpenRead(path);
      var manifest = JsonSerializer.Deserialize<MediaToolManifest>(stream)
        ?? throw new MediaToolException($"Media tool manifest '{path}' is empty.");
      manifest.Validate(path);
      return manifest;
    }
    catch (MediaToolException)
    {
      throw;
    }
    catch (Exception exception) when (exception is IOException or JsonException)
    {
      throw new MediaToolException($"Unable to load media tool manifest '{path}': {exception.Message}", exception);
    }
  }

  private void Validate(string path)
  {
    if (SchemaVersion != 1)
    {
      throw new MediaToolException($"Media tool manifest '{path}' uses unsupported schema version {SchemaVersion}.");
    }

    if (string.IsNullOrWhiteSpace(Provider) ||
        string.IsNullOrWhiteSpace(Release) ||
        string.IsNullOrWhiteSpace(Asset) ||
        string.IsNullOrWhiteSpace(Variant) ||
        string.IsNullOrWhiteSpace(ExpectedVersionPrefix))
    {
      throw new MediaToolException($"Media tool manifest '{path}' is missing a required value.");
    }

    if (Sha256 is null ||
        Sha256.Length != 64 ||
        Sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
    {
      throw new MediaToolException(
        $"Media tool manifest '{path}' must contain a 64-character lowercase hexadecimal SHA-256 digest.");
    }

    if (ExpectedExecutables is null ||
        !ExpectedExecutables.SequenceEqual(["ffmpeg.exe", "ffprobe.exe"], StringComparer.Ordinal))
    {
      throw new MediaToolException(
        $"Media tool manifest '{path}' must list ffmpeg.exe and ffprobe.exe as expected executables.");
    }
  }
}
