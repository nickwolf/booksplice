namespace BookSplice.FFmpeg.Execution;

internal sealed class SegmentedAssemblyException(string message) : Exception(message);

internal static class AdtsFrames
{
  private const int HeaderBytes = 7;

  // Frame sizes in file order. Anything that is not a clean run of ADTS frames is an error, because a bad cut would be silent.
  public static IReadOnlyList<int> ReadLengths(string path)
  {
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
    var lengths = new List<int>();
    var header = new byte[HeaderBytes];
    var offset = 0L;
    while (offset < stream.Length)
    {
      stream.Position = offset;
      if (stream.Read(header, 0, HeaderBytes) != HeaderBytes) throw new SegmentedAssemblyException("An ADTS frame header is truncated.");
      var length = Length(header);
      if (offset + length > stream.Length) throw new SegmentedAssemblyException("An ADTS frame runs past the end of the file.");
      lengths.Add(length);
      offset += length;
    }
    return lengths;
  }

  public static int Length(ReadOnlySpan<byte> header)
  {
    if (header.Length < HeaderBytes || header[0] != 0xFF || (header[1] & 0xF0) != 0xF0) throw new SegmentedAssemblyException("The ADTS sync word is missing.");
    var length = ((header[3] & 3) << 11) | (header[4] << 3) | (header[5] >> 5);
    if (length < HeaderBytes) throw new SegmentedAssemblyException("An ADTS frame length is invalid.");
    return length;
  }

  // Copies frames first..last (inclusive) of a file whose frame sizes are already known.
  public static void Append(string path, IReadOnlyList<int> lengths, int first, int last, Stream destination)
  {
    if (first < 0 || last >= lengths.Count || first > last + 1) throw new SegmentedAssemblyException("The frames to keep are outside the segment.");
    long offset = 0;
    for (var index = 0; index < first; index++) offset += lengths[index];
    long count = 0;
    for (var index = first; index <= last; index++) count += lengths[index];
    using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
    source.Position = offset;
    var buffer = new byte[1 << 16];
    while (count > 0)
    {
      var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
      if (read <= 0) throw new SegmentedAssemblyException("A segment ended early.");
      destination.Write(buffer, 0, read);
      count -= read;
    }
  }
}
