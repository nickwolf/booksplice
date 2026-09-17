using TagLib.Mpeg4;

namespace BookSplice.FFmpeg.Commands;

public sealed record Mp4MetadataWritePlan(IReadOnlyDictionary<string, string> NativeFields, IReadOnlyDictionary<string, string> FreeformFields);

public sealed class Mp4MetadataWriter : IMp4MetadataWriter
{
  public const string FreeformMean = "com.apple.iTunes";

  public void Write(string path, IReadOnlyDictionary<string, string> tags)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    ArgumentNullException.ThrowIfNull(tags);
    var plan = CreatePlan(tags);
    using var file = TagLib.File.Create(path);
    var tag = (AppleTag)file.GetTag(TagLib.TagTypes.Apple, true);
    ApplyNativeFields(tag, plan.NativeFields);
    foreach (var field in plan.FreeformFields) tag.SetDashBox(FreeformMean, field.Key, field.Value);
    file.Save();
  }

  public static Mp4MetadataWritePlan CreatePlan(IReadOnlyDictionary<string, string> tags)
  {
    ArgumentNullException.ThrowIfNull(tags);
    var native = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var freeform = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var tag in tags)
    {
      if (IsNativeField(tag.Key)) native[tag.Key] = tag.Value;
      else freeform[tag.Key] = tag.Value;
    }
    return new Mp4MetadataWritePlan(native, freeform);
  }

  private static bool IsNativeField(string key) => key.Equals("TITLE", StringComparison.OrdinalIgnoreCase) || key.Equals("ALBUM", StringComparison.OrdinalIgnoreCase) || key.Equals("ARTIST", StringComparison.OrdinalIgnoreCase) || key.Equals("ALBUMARTIST", StringComparison.OrdinalIgnoreCase) || key.Equals("album_artist", StringComparison.OrdinalIgnoreCase) || key.Equals("COMPOSER", StringComparison.OrdinalIgnoreCase);

  private static void ApplyNativeFields(AppleTag tag, IReadOnlyDictionary<string, string> fields)
  {
    if (fields.TryGetValue("TITLE", out var title)) tag.Title = title;
    if (fields.TryGetValue("ALBUM", out var album)) tag.Album = album;
    if (fields.TryGetValue("ARTIST", out var artist)) tag.Performers = [artist];
    if (fields.TryGetValue("ALBUMARTIST", out var albumArtist) || fields.TryGetValue("album_artist", out albumArtist)) tag.AlbumArtists = [albumArtist];
    if (fields.TryGetValue("COMPOSER", out var composer)) tag.Composers = [composer];
  }
}
