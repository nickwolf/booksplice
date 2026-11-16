using System.Buffers.Binary;
using System.Text;

namespace BookSplice.FFmpeg.Commands;

// One child of moov/udta/meta/ilst. Raw is the whole box, header included.
internal sealed record Mp4MetadataItem(string Type, string? Mean, string? Name, byte[] Raw, IReadOnlyList<Mp4MetadataData> Data)
{
  public bool IsFreeform => Type == "----";
  public bool IsITunesFreeform(string key) => IsFreeform && string.Equals(Mean, Mp4MetadataWriter.FreeformMean, StringComparison.Ordinal) && string.Equals(Name, key, StringComparison.OrdinalIgnoreCase);
}

internal sealed record Mp4MetadataData(uint TypeCode, byte[] Payload)
{
  public string Text => Encoding.UTF8.GetString(Payload);
}

// Raw access to the iTunes metadata box, used where the bytes must survive exactly.
internal static class Mp4MetadataBoxes
{
  private const int MaximumMoovBytes = 512 * 1024 * 1024;

  public static string? ReadMajorBrand(string path)
  {
    using var stream = OpenRead(path);
    var ftyp = FindBox(stream, 0, stream.Length, "ftyp");
    if (ftyp is not { PayloadLength: >= 4 } box) return null;
    var brand = new byte[4];
    stream.Position = box.PayloadOffset;
    stream.ReadExactly(brand);
    return brand.All(value => value is >= 0x20 and < 0x7F) ? Encoding.ASCII.GetString(brand) : null;
  }

  public static byte[]? ReadMetaBox(string path)
  {
    using var stream = OpenRead(path);
    var meta = FindNested(stream, "moov", "udta", "meta");
    if (meta is null) return null;
    var raw = new byte[checked((int)(meta.Value.EndOffset - meta.Value.Offset))];
    stream.Position = meta.Value.Offset;
    stream.ReadExactly(raw);
    return raw;
  }

  public static IReadOnlyList<Mp4MetadataItem> ReadItems(string path)
  {
    var meta = ReadMetaBox(path);
    if (meta is null) return [];
    using var stream = new MemoryStream(meta, writable: false);
    var metaBox = FindBox(stream, 0, stream.Length, "meta") ?? throw new InvalidDataException("The MP4 meta box is invalid.");
    var ilst = FindBox(stream, metaBox.PayloadOffset + 4, metaBox.EndOffset, "ilst");
    if (ilst is null) return [];
    return Children(stream, ilst.Value).Select(item => ReadItem(meta, stream, item)).ToArray();
  }

  // Replaces moov/udta/meta with the given box (or removes it when null). The file must end with moov, so no media offsets move.
  public static void ReplaceMetaBox(string path, byte[]? metaBox)
  {
    using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var moov = FindBox(stream, 0, stream.Length, "moov") ?? throw new InvalidDataException("The MP4 file has no moov box.");
    if (moov.EndOffset != stream.Length) throw new InvalidDataException("The MP4 moov box is not at the end of the file.");
    if (moov.EndOffset - moov.Offset > MaximumMoovBytes) throw new InvalidDataException("The MP4 moov box is too large.");
    var moovChildren = Children(stream, moov).ToArray();
    var rebuilt = new MemoryStream();
    var wroteUdta = false;
    foreach (var child in moovChildren)
    {
      if (child.Type != "udta") { rebuilt.Write(ReadRaw(stream, child)); continue; }
      wroteUdta = true;
      var udtaChildren = new MemoryStream();
      var wroteMeta = false;
      foreach (var inner in Children(stream, child))
      {
        if (inner.Type != "meta") { udtaChildren.Write(ReadRaw(stream, inner)); continue; }
        if (!wroteMeta && metaBox is not null) udtaChildren.Write(metaBox);
        wroteMeta = true;
      }
      if (!wroteMeta && metaBox is not null) udtaChildren.Write(metaBox);
      WriteBox(rebuilt, "udta", udtaChildren.ToArray());
    }
    if (!wroteUdta && metaBox is not null)
    {
      var udta = new MemoryStream();
      WriteBox(udta, "udta", metaBox);
      rebuilt.Write(udta.ToArray());
    }
    stream.SetLength(moov.Offset);
    stream.Position = moov.Offset;
    WriteBox(stream, "moov", rebuilt.ToArray());
    stream.Flush(flushToDisk: true);
  }

  private static Mp4MetadataItem ReadItem(byte[] meta, Stream stream, Box item)
  {
    var raw = meta.AsSpan((int)item.Offset, (int)(item.EndOffset - item.Offset)).ToArray();
    string? mean = null;
    string? name = null;
    var data = new List<Mp4MetadataData>();
    foreach (var child in Children(stream, item))
    {
      var payload = meta.AsSpan((int)child.PayloadOffset, (int)child.PayloadLength);
      if (child.Type is "mean" or "name")
      {
        var text = payload.Length >= 4 ? Encoding.UTF8.GetString(payload[4..]) : "";
        if (child.Type == "mean") mean = text; else name = text;
      }
      else if (child.Type == "data" && payload.Length >= 8)
        data.Add(new Mp4MetadataData(BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x00FF_FFFF, payload[8..].ToArray()));
    }
    return new Mp4MetadataItem(item.Type, mean, name, raw, data);
  }

  private static byte[] ReadRaw(Stream stream, Box box)
  {
    var raw = new byte[checked((int)(box.EndOffset - box.Offset))];
    stream.Position = box.Offset;
    stream.ReadExactly(raw);
    return raw;
  }

  private static void WriteBox(Stream output, string type, byte[] payload)
  {
    var size = checked((uint)(payload.Length + 8));
    Span<byte> header = stackalloc byte[8];
    BinaryPrimitives.WriteUInt32BigEndian(header, size);
    Encoding.ASCII.GetBytes(type, header[4..]);
    output.Write(header);
    output.Write(payload);
  }

  private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

  private static Box? FindNested(Stream stream, params string[] path)
  {
    long start = 0;
    var end = stream.Length;
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

  private static Box? FindBox(Stream stream, long start, long end, string type)
    => Children(stream, start, end).Cast<Box?>().FirstOrDefault(box => box!.Value.Type == type);

  private static IEnumerable<Box> Children(Stream stream, Box parent) => Children(stream, parent.PayloadOffset, parent.EndOffset);

  private static IEnumerable<Box> Children(Stream stream, long start, long end)
  {
    var header = new byte[16];
    for (var offset = start; offset <= end - 8;)
    {
      stream.Position = offset;
      stream.ReadExactly(header, 0, 8);
      var size32 = BinaryPrimitives.ReadUInt32BigEndian(header);
      var type = Encoding.Latin1.GetString(header, 4, 4);
      long headerSize = 8;
      long size;
      if (size32 == 1)
      {
        if (offset > end - 16) throw new InvalidDataException("The MP4 box header is truncated.");
        stream.ReadExactly(header, 8, 8);
        var size64 = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
        if (size64 > long.MaxValue) throw new InvalidDataException("The MP4 box is too large.");
        size = (long)size64;
        headerSize = 16;
      }
      else size = size32 == 0 ? end - offset : size32;
      if (size < headerSize || size > end - offset) throw new InvalidDataException("The MP4 box size is invalid.");
      yield return new Box(type, offset, offset + headerSize, offset + size);
      offset += size;
    }
  }

  private readonly record struct Box(string Type, long Offset, long PayloadOffset, long EndOffset)
  {
    public long PayloadLength => EndOffset - PayloadOffset;
  }
}
