using System.Buffers.Binary;
using System.Text;
using BookSplice.Core.Analysis;

namespace BookSplice.FFmpeg.Probing;

internal static class NeroChapterReader
{
  private const int MaximumPayloadBytes = 64 * 1024 * 1024;

  public static IReadOnlyList<MediaChapter>? Read(string path, decimal? finalDuration, IReadOnlyList<MediaChapter> nativeChapters)
  {
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    var box = FindNestedBox(stream, "moov", "udta", "chpl");
    if (box is null) return null;
    if (box.Value.PayloadLength is < 9 or > MaximumPayloadBytes) throw new InvalidDataException("The Nero chapter payload has an invalid size.");
    var payload = new byte[(int)box.Value.PayloadLength];
    stream.Position = box.Value.PayloadOffset;
    stream.ReadExactly(payload);

    var count = payload[8];
    var offset = 9;
    var entries = new List<(decimal Start, string Title)>(count);
    for (var index = 0; index < count; index++)
    {
      if (offset > payload.Length - 9) throw new InvalidDataException("The Nero chapter payload is truncated.");
      var start = checked((decimal)BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(offset, 8)) / 10_000_000m);
      offset += 8;
      var titleLength = payload[offset++];
      if (offset > payload.Length - titleLength) throw new InvalidDataException("The Nero chapter title is truncated.");
      entries.Add((start, Encoding.UTF8.GetString(payload, offset, titleLength)));
      offset += titleLength;
    }
    if (offset != payload.Length) throw new InvalidDataException("The Nero chapter payload contains trailing data.");
    if (entries.Count == 0 || nativeChapters.Count > entries.Count) return null;
    var lastStart = entries[^1].Start;
    var matchingNativeEnds = nativeChapters.Where(chapter => chapter.StartTime == lastStart && chapter.EndTime > lastStart).Select(chapter => chapter.EndTime).ToArray();
    var finalEnd = matchingNativeEnds.Length > 0 ? matchingNativeEnds.Min() : finalDuration ?? throw new InvalidDataException("The Nero chapter payload has no final media duration.");

    return entries.Select((entry, index) => new MediaChapter(
      index,
      entry.Start,
      index + 1 < entries.Count ? entries[index + 1].Start : finalEnd,
      new Rational(1, 10_000_000),
      new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = entry.Title })).ToArray();
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
      var size32 = BinaryPrimitives.ReadUInt32BigEndian(header);
      var type = Encoding.ASCII.GetString(header[4..8]);
      long headerSize = 8;
      long size;
      if (size32 == 1)
      {
        if (offset > end - 16) throw new InvalidDataException("The MP4 box header is truncated.");
        stream.ReadExactly(header[8..16]);
        var size64 = BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
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

  private readonly record struct Box(long PayloadOffset, long EndOffset)
  {
    public long PayloadLength => EndOffset - PayloadOffset;
  }
}
