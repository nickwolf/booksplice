using System.Buffers.Binary;

namespace BookSplice.FFmpeg.Preview;

/// <summary>Joins PCM WAV files that share one format into a single canonical WAV, streaming the sample data.</summary>
public static class WavJoiner
{
  private const int HeaderSize = 44;
  private const uint UnknownSize = 0xFFFFFFFF;

  public static void Join(IReadOnlyList<string> inputs, string output)
  {
    ArgumentNullException.ThrowIfNull(inputs);
    if (inputs.Count == 0) throw new ArgumentException("At least one input is required.", nameof(inputs));
    var parsed = new List<(string Path, long DataOffset, long DataLength)>();
    byte[]? format = null;
    foreach (var path in inputs)
    {
      using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      var (fmt, offset, length) = Parse(stream, path);
      if (format is null) format = fmt;
      else if (!format.AsSpan().SequenceEqual(fmt)) throw new InvalidDataException("The WAV inputs do not share one audio format.");
      parsed.Add((path, offset, length));
    }

    long total = 0;
    foreach (var item in parsed) total = checked(total + item.DataLength);
    if (format!.Length != 16) throw new InvalidDataException("Only plain PCM WAV files can be joined.");
    if (total > uint.MaxValue - 37) throw new InvalidDataException("The joined WAV is too large.");

    using var target = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
    var header = new byte[HeaderSize];
    "RIFF"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(36 + total + (total & 1)));
    "WAVEfmt "u8.CopyTo(header.AsSpan(8));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
    format.AsSpan(0, 16).CopyTo(header.AsSpan(20));
    "data"u8.CopyTo(header.AsSpan(36));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)total);
    target.Write(header);

    var buffer = new byte[81920];
    foreach (var item in parsed)
    {
      using var source = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
      source.Seek(item.DataOffset, SeekOrigin.Begin);
      var remaining = item.DataLength;
      while (remaining > 0)
      {
        var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
        if (read <= 0) throw new InvalidDataException("A WAV input ended early.");
        target.Write(buffer, 0, read);
        remaining -= read;
      }
    }
    if (total % 2 != 0) target.WriteByte(0);
  }

  private static (byte[] Format, long DataOffset, long DataLength) Parse(FileStream stream, string path)
  {
    var length = stream.Length;
    Span<byte> head = stackalloc byte[12];
    if (stream.Read(head) != 12 || !head[..4].SequenceEqual("RIFF"u8) || !head[8..12].SequenceEqual("WAVE"u8))
      throw new InvalidDataException($"'{System.IO.Path.GetFileName(path)}' is not a RIFF/WAVE file.");
    byte[]? format = null;
    Span<byte> chunk = stackalloc byte[8];
    while (stream.Position + 8 <= length)
    {
      stream.ReadExactly(chunk);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
      var body = stream.Position;
      if (chunk[..4].SequenceEqual("data"u8))
      {
        if (format is null) throw new InvalidDataException("The WAV data chunk came before the format chunk.");
        var available = length - body;
        var dataLength = size == 0 || size == UnknownSize || size > available ? available : size;
        return (format, body, dataLength);
      }
      if (size > length - body) throw new InvalidDataException("A WAV chunk runs past the end of the file.");
      if (chunk[..4].SequenceEqual("fmt "u8))
      {
        if (size < 16) throw new InvalidDataException("The WAV format chunk is too short.");
        format = new byte[size];
        stream.ReadExactly(format);
      }
      stream.Position = body + size + (size & 1);
    }
    throw new InvalidDataException("The WAV file has no data chunk.");
  }
}
