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

internal sealed record Mp4RawBox(string Type, byte[] Raw);

// The children of moov/udta and the moov-level meta box (QuickTime mdta keys), raw.
internal sealed record Mp4UserData(IReadOnlyList<Mp4RawBox> UdtaChildren, byte[]? MoovMeta)
{
  // What an edit has to leave alone byte for byte: every udta child except chpl (written again from the chapter titles) and
  // meta (whose tags are checked one by one), then the moov-level meta box.
  public IReadOnlyList<byte[]> Preserved => [.. UdtaChildren.Where(box => box.Type is not ("chpl" or "meta")).Select(box => box.Raw), .. MoovMeta is null ? Array.Empty<byte[]>() : [MoovMeta]];
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

  public static Mp4UserData ReadUserData(string path)
  {
    using var stream = OpenRead(path);
    var moov = FindBox(stream, 0, stream.Length, "moov") ?? throw new InvalidDataException("The MP4 file has no moov box.");
    var moovChildren = Children(stream, moov).ToArray();
    var udta = moovChildren.Where(box => box.Type == "udta").Cast<Box?>().FirstOrDefault();
    var udtaChildren = udta is null ? [] : Children(stream, udta.Value).ToArray().Select(box => new Mp4RawBox(box.Type, ReadRaw(stream, box))).ToArray();
    var meta = moovChildren.Where(box => box.Type == "meta").Cast<Box?>().FirstOrDefault();
    return new Mp4UserData(udtaChildren, meta is null ? null : ReadRaw(stream, meta.Value));
  }

  // TagLib writes back the udta children it doesn't parse (chpl, Xtra, ...) with their sizes kept but their bytes taken from
  // further into the file (24 bytes on, in the files tested), so the caller keeps them from before the save and this puts
  // them back. Every child but meta must still be there with the same type and size.
  public static void RestoreUserDataChildren(string path, IReadOnlyList<Mp4RawBox> before)
  {
    ArgumentNullException.ThrowIfNull(before);
    var kept = before.Where(box => box.Type != "meta").ToArray();
    using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var udta = FindNested(stream, "moov", "udta");
    var current = udta is null ? [] : Children(stream, udta.Value).Where(box => box.Type != "meta").ToArray();
    if (current.Length != kept.Length) throw new InvalidDataException("The MP4 user data boxes changed while writing metadata.");
    for (var index = 0; index < kept.Length; index++)
    {
      if (current[index].Type != kept[index].Type || current[index].EndOffset - current[index].Offset != kept[index].Raw.Length)
        throw new InvalidDataException("The MP4 user data boxes changed while writing metadata.");
    }
    for (var index = 0; index < kept.Length; index++)
    {
      stream.Position = current[index].Offset;
      stream.Write(kept[index].Raw);
    }
    stream.Flush(flushToDisk: true);
  }

  // When udta ends the file (moov at the end, as ffmpeg writes it without faststart), TagLib's read of its last child runs off
  // the end and the box comes back shorter (a 37-byte chpl came back as 13 bytes). A free box after it keeps that read inside
  // the file, so the box keeps its size and RestoreUserDataChildren can put the bytes back.
  public static void PadAfterFinalUserData(string path)
  {
    using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var udta = FindNested(stream, "moov", "udta");
    if (udta is null || udta.Value.EndOffset != stream.Length) return;
    var largest = Children(stream, udta.Value).Select(box => box.EndOffset - box.Offset).DefaultIfEmpty(0).Max();
    var padding = new byte[checked((int)largest + 16)];
    BinaryPrimitives.WriteUInt32BigEndian(padding, (uint)padding.Length);
    "free"u8.CopyTo(padding.AsSpan(4));
    stream.Position = stream.Length;
    stream.Write(padding);
  }

  // Rebuilds moov so its udta holds the original's children in the original's order, with the file's own chpl in place of the
  // original's, and its moov-level meta box is the original's (or none). The file must end with moov, so no media offsets move.
  public static void RestoreUserData(string path, Mp4UserData original)
  {
    ArgumentNullException.ThrowIfNull(original);
    using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var moov = FindBox(stream, 0, stream.Length, "moov") ?? throw new InvalidDataException("The MP4 file has no moov box.");
    if (moov.EndOffset != stream.Length) throw new InvalidDataException("The MP4 moov box is not at the end of the file.");
    if (moov.EndOffset - moov.Offset > MaximumMoovBytes) throw new InvalidDataException("The MP4 moov box is too large.");
    var moovChildren = Children(stream, moov).ToArray();
    var udta = moovChildren.Where(box => box.Type == "udta").Cast<Box?>().FirstOrDefault();
    var chapters = udta is null ? null : Children(stream, udta.Value).ToArray().Where(box => box.Type == "chpl").Select(box => ReadRaw(stream, box)).FirstOrDefault();
    var udtaPayload = new MemoryStream();
    var wroteChapters = false;
    foreach (var box in original.UdtaChildren)
    {
      if (box.Type != "chpl") { udtaPayload.Write(box.Raw); continue; }
      if (chapters is not null && !wroteChapters) udtaPayload.Write(chapters);
      wroteChapters = true;
    }
    if (chapters is not null && !wroteChapters) udtaPayload.Write(chapters);
    var rebuilt = new MemoryStream();
    foreach (var child in moovChildren.Where(box => box.Type is not ("udta" or "meta"))) rebuilt.Write(ReadRaw(stream, child));
    if (udtaPayload.Length > 0) WriteBox(rebuilt, "udta", udtaPayload.ToArray());
    if (original.MoovMeta is not null) rebuilt.Write(original.MoovMeta);
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
