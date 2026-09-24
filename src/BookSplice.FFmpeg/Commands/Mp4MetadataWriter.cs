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
    var chapterPayload = ReadNeroChapterPayload(path);
    using (var file = TagLib.File.Create(path))
    {
      var tag = (AppleTag)file.GetTag(TagLib.TagTypes.Apple, true);
      ApplyNativeFields(tag, plan.NativeFields);
      foreach (var field in plan.FreeformFields) tag.SetDashBox(FreeformMean, field.Key, field.Value);
      file.Save();
    }
    RestoreNeroChapterPayload(path, chapterPayload);
  }

  private const int MaximumChapterPayloadBytes = 64 * 1024 * 1024;

  private static byte[]? ReadNeroChapterPayload(string path)
  {
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    var chapter = FindNestedBox(stream, "moov", "udta", "chpl");
    if (chapter is null) return null;
    var length = chapter.Value.PayloadLength;
    if (length > MaximumChapterPayloadBytes) throw new InvalidDataException("The MP4 chapter box exceeds the safe preservation limit.");
    var payload = new byte[(int)length];
    stream.Position = chapter.Value.PayloadOffset;
    stream.ReadExactly(payload);
    return payload;
  }

  private static void RestoreNeroChapterPayload(string path, byte[]? originalPayload)
  {
    if (originalPayload is null) return;
    using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var chapter = FindNestedBox(stream, "moov", "udta", "chpl") ?? throw new InvalidDataException("The MP4 chapter box was removed while writing metadata.");
    if (chapter.PayloadLength != originalPayload.Length) throw new InvalidDataException("The MP4 chapter box size changed while writing metadata.");
    stream.Position = chapter.PayloadOffset;
    stream.Write(originalPayload);
  }

  private static Box? FindNestedBox(Stream stream, params string[] path)
  {
    long start = 0;
    long end = stream.Length;
    Box? match = null;
    foreach (var type in path)
    {
      match = FindBox(stream, start, end, type);
      if (match is null) return null;
      start = match.Value.PayloadOffset;
      end = match.Value.EndOffset;
    }
    return match;
  }

  private static Box? FindBox(Stream stream, long start, long end, string expectedType)
  {
    Span<byte> header = stackalloc byte[16];
    for (var offset = start; offset <= end - 8;)
    {
      stream.Position = offset;
      stream.ReadExactly(header[..8]);
      var size32 = ReadUInt32BigEndian(header);
      var type = System.Text.Encoding.ASCII.GetString(header[4..8]);
      long headerSize = 8;
      long size;
      if (size32 == 1)
      {
        if (offset > end - 16) throw new InvalidDataException("The MP4 box header is truncated.");
        stream.ReadExactly(header[8..16]);
        var size64 = ReadUInt64BigEndian(header[8..16]);
        if (size64 > long.MaxValue) throw new InvalidDataException("The MP4 box is too large.");
        size = (long)size64;
        headerSize = 16;
      }
      else size = size32 == 0 ? end - offset : size32;
      if (size < headerSize || size > end - offset) throw new InvalidDataException("The MP4 box size is invalid.");
      var box = new Box(offset + headerSize, offset + size);
      if (string.Equals(type, expectedType, StringComparison.Ordinal)) return box;
      offset += size;
    }
    return null;
  }

  private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> value) =>
    ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];

  private static ulong ReadUInt64BigEndian(ReadOnlySpan<byte> value) =>
    ((ulong)ReadUInt32BigEndian(value) << 32) | ReadUInt32BigEndian(value[4..]);

  private readonly record struct Box(long PayloadOffset, long EndOffset)
  {
    public long PayloadLength => EndOffset - PayloadOffset;
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
