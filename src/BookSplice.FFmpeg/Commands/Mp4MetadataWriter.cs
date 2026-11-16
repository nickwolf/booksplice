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

  // Edits an existing file in place. An empty value removes the tag. Keys a player reads from a native atom go to that atom,
  // and a com.apple.iTunes freeform box with the same name (a converted file can carry both) gets the same value.
  public static void Edit(string path, IReadOnlyDictionary<string, string> tags)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    ArgumentNullException.ThrowIfNull(tags);
    var freeformNames = Mp4MetadataBoxes.ReadItems(path).Where(item => item.IsFreeform && item.Mean == FreeformMean && item.Name is not null).Select(item => item.Name!).ToArray();
    var userData = Mp4MetadataBoxes.ReadUserData(path).UdtaChildren;
    Mp4MetadataBoxes.PadAfterFinalUserData(path);
    using (var file = TagLib.File.Create(path))
    {
      var tag = (AppleTag)file.GetTag(TagLib.TagTypes.Apple, true);
      foreach (var (key, value) in tags)
      {
        var atom = EditAtom(key);
        if (atom == MediaTypeAtom)
        {
          if (value.Length == 0) tag.ClearData(AtomId(atom));
          else tag.SetData(AtomId(atom), new TagLib.ByteVector(ParseMediaType(value)), (uint)AppleDataBox.FlagType.ForTempo);
        }
        else if (atom is not null) tag.SetText(AtomId(atom), value);
        var names = freeformNames.Where(name => string.Equals(name, key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (atom is null && names.Length == 0) names = [key];
        foreach (var name in names) tag.SetDashBox(FreeformMean, name, value);
      }
      file.Save();
    }
    Mp4MetadataBoxes.RestoreUserDataChildren(path, userData);
  }

  public const string MediaTypeAtom = "stik";

  // The atoms ffmpeg's MP4 muxer writes for these keys, so an edit lands where players and ffprobe read the value.
  private static readonly Dictionary<string, string> EditAtoms = new(StringComparer.OrdinalIgnoreCase)
  {
    ["title"] = "\u00A9nam",
    ["album"] = "\u00A9alb",
    ["artist"] = "\u00A9ART",
    ["album_artist"] = "aART",
    ["albumartist"] = "aART",
    ["composer"] = "\u00A9wrt",
    ["genre"] = "\u00A9gen",
    ["date"] = "\u00A9day",
    ["comment"] = "\u00A9cmt",
    ["description"] = "desc",
    ["synopsis"] = "ldes",
    ["copyright"] = "cprt",
    ["grouping"] = "\u00A9grp",
    ["lyrics"] = "\u00A9lyr",
    ["media_type"] = MediaTypeAtom,
  };

  public static string? EditAtom(string key) => EditAtoms.GetValueOrDefault(key);

  public static bool IsValidMediaType(string value) => value.Length == 0 || byte.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);

  private static byte ParseMediaType(string value) => byte.Parse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);

  private static TagLib.ByteVector AtomId(string atom) => TagLib.ByteVector.FromString(atom, TagLib.StringType.Latin1);

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
