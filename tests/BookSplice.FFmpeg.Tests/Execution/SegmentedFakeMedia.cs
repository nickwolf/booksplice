using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BookSplice.FFmpeg.Execution;

namespace BookSplice.FFmpeg.Tests.Execution;

// Stands in for ffmpeg in the segmented pipeline: reports a length per source, writes ADTS segments of the size the encoder contract implies, and writes an MP4 with one edit list.
internal static partial class SegmentedFakeMedia
{
  public static long SamplesOf(int sourceIndex) => 441_000 + 100 * sourceIndex;

  public static bool TryHandle(ProcessSpec spec, IProgress<string>? progress, out string? standardError, int frameDelta = 0, bool corrupt = false)
  {
    standardError = null;
    var arguments = spec.Arguments.ToList();
    if (arguments.Contains("-itsoffset"))
    {
      File.WriteAllBytes(arguments[^1], Mp4(1, 28_224_000, 44_100, 7_938_048 + 1024, 0));
      return true;
    }
    var format = arguments.IndexOf("-f");
    if (format >= 0 && arguments[format + 1] == "null")
    {
      var match = SourceName().Match(arguments[arguments.IndexOf("-i") + 1]);
      standardError = $"[out#0/null @ 000001]   Output stream #0:0 (audio): 400 frames encoded ({SamplesOf(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))} samples); 400 packets muxed (1 bytes); ";
      return true;
    }
    if (format >= 0 && arguments[format + 1] == "adts")
    {
      var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
      var trim = Trim().Match(graph);
      var samples = long.Parse(trim.Groups[2].Value, CultureInfo.InvariantCulture) - long.Parse(trim.Groups[1].Value, CultureInfo.InvariantCulture);
      progress?.Report("out_time_us=" + samples * 1_000_000L / 44_100);
      File.WriteAllBytes(arguments[^1], corrupt ? [1, 2, 3, 4, 5, 6, 7, 8] : Adts((int)((samples + 1023) / 1024) + 1 + frameDelta));
      return true;
    }
    return false;
  }

  public static byte[] Adts(int frames)
  {
    const int length = 16;
    var bytes = new byte[frames * length];
    for (var frame = 0; frame < frames; frame++)
    {
      var at = frame * length;
      bytes[at] = 0xFF;
      bytes[at + 1] = 0xF1;
      bytes[at + 2] = 0x4C;
      bytes[at + 3] = (byte)(0x40 | (length >> 11 & 3));
      bytes[at + 4] = (byte)(length >> 3 & 0xFF);
      bytes[at + 5] = (byte)((length & 7) << 5 | 0x1F);
      bytes[at + 6] = 0xFC;
      bytes[at + 7] = (byte)frame;
    }
    return bytes;
  }

  // A file with ftyp, moov(mvhd, trak(edts(elst), mdia(mdhd))) and mdat, in version 0 or 1 boxes..
  public static byte[] Mp4(int version, uint movieScale, uint mediaScale, long segmentDuration, long mediaTime)
  {
    var mvhd = Box("mvhd", Concat(Header(version), Time(version, 2), Time(version, 2), U32(movieScale), Time(version, 0), new byte[80]));
    var mdhd = Box("mdhd", Concat(Header(version), Time(version, 2), Time(version, 2), U32(mediaScale), Time(version, 0), new byte[4]));
    var elst = Box("elst", Concat(Header(version), U32(1), Time(version, segmentDuration), version == 1 ? BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(mediaTime)) : U32((uint)mediaTime), [0, 1, 0, 0]));
    var trak = Box("trak", Concat(Box("edts", elst), Box("mdia", mdhd)));
    return Concat(Box("ftyp", Encoding.ASCII.GetBytes("M4A \0\0\0\0M4A ")), Box("moov", Concat(mvhd, trak)), Box("mdat", new byte[16]));
  }

  public static (long Duration, long MediaTime) ReadElst(byte[] file, int version)
  {
    var at = IndexOf(file, "elst") + 4 + 8;
    return version == 1
      ? ((long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(at)), BinaryPrimitives.ReadInt64BigEndian(file.AsSpan(at + 8)))
      : (BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at)), BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at + 4)));
  }

  private static byte[] Header(int version) => [(byte)version, 0, 0, 0];
  private static int IndexOf(byte[] file, string type) => file.AsSpan().IndexOf(Encoding.ASCII.GetBytes(type));
  private static byte[] Time(int version, long value) => version == 1 ? BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(value)) : U32((uint)value);
  private static byte[] U32(uint value) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); return bytes; }
  private static byte[] Box(string type, byte[] payload) => Concat(U32((uint)payload.Length + 8), Encoding.ASCII.GetBytes(type), payload);
  private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];

  [GeneratedRegex(@"source-(\d+)\.mp3")]
  private static partial Regex SourceName();
  [GeneratedRegex(@"atrim=start_sample=(\d+):end_sample=(\d+)")]
  private static partial Regex Trim();
}
