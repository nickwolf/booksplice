using System.Buffers.Binary;
using BookSplice.FFmpeg.Preview;

namespace BookSplice.FFmpeg.Tests.Preview;

public sealed class WavJoinerTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("booksplice-wavjoin-").FullName;

  public void Dispose() => Directory.Delete(_root, true);

  private static byte[] Fmt(int rate = 44100)
  {
    var bytes = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, 1);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)rate);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)rate * 4);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 4);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 16);
    return bytes;
  }

  private static void Chunk(MemoryStream stream, string id, byte[] body, uint? declared = null, bool pad = true)
  {
    stream.Write(System.Text.Encoding.ASCII.GetBytes(id));
    var size = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(size, declared ?? (uint)body.Length);
    stream.Write(size);
    stream.Write(body);
    if (pad && body.Length % 2 == 1) stream.WriteByte(0);
  }

  private string Wav(string name, byte[] data, byte[]? fmt = null, bool list = false, uint? dataSize = null, bool oddChunk = false)
  {
    var body = new MemoryStream();
    body.Write("WAVE"u8);
    Chunk(body, "fmt ", fmt ?? Fmt());
    if (list) Chunk(body, "LIST", System.Text.Encoding.ASCII.GetBytes("INFOabc"));
    if (oddChunk) Chunk(body, "junk", [1, 2, 3]);
    Chunk(body, "data", data, dataSize);
    var path = Path.Combine(_root, name);
    using var file = File.Create(path);
    file.Write("RIFF"u8);
    var size = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
    file.Write(size);
    body.WriteTo(file);
    return path;
  }

  private static byte[] Pattern(int length, byte seed) => Enumerable.Range(0, length).Select(index => (byte)(seed + index)).ToArray();

  private static void AssertCanonical(byte[] file, byte[] expectedData)
  {
    Assert.Equal(44 + expectedData.Length, file.Length);
    Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(file, 0, 4));
    Assert.Equal((uint)(36 + expectedData.Length), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)));
    Assert.Equal("WAVEfmt ", System.Text.Encoding.ASCII.GetString(file, 8, 8));
    Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16)));
    Assert.Equal(Fmt(), file.AsSpan(20, 16).ToArray());
    Assert.Equal("data", System.Text.Encoding.ASCII.GetString(file, 36, 4));
    Assert.Equal((uint)expectedData.Length, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(40)));
    Assert.Equal(expectedData, file.AsSpan(44).ToArray());
  }

  [Fact]
  public void TwoInputsJoinToSummedDataWithCanonicalHeader()
  {
    var a = Pattern(400, 1);
    var b = Pattern(120, 77);
    var output = Path.Combine(_root, "out.wav");
    WavJoiner.Join([Wav("a.wav", a), Wav("b.wav", b)], output);
    AssertCanonical(File.ReadAllBytes(output), [.. a, .. b]);
  }

  [Fact]
  public void SingleInputIsCopiedAsCanonical()
  {
    var a = Pattern(64, 9);
    var output = Path.Combine(_root, "out.wav");
    WavJoiner.Join([Wav("a.wav", a, list: true)], output);
    AssertCanonical(File.ReadAllBytes(output), a);
  }

  [Fact]
  public void ListAndOddPaddedChunksAreSkipped()
  {
    var a = Pattern(40, 3);
    var output = Path.Combine(_root, "out.wav");
    WavJoiner.Join([Wav("a.wav", a, list: true, oddChunk: true)], output);
    AssertCanonical(File.ReadAllBytes(output), a);
  }

  [Fact]
  public void MismatchedFormatIsRejected()
  {
    var output = Path.Combine(_root, "out.wav");
    Assert.Throws<InvalidDataException>(() => WavJoiner.Join([Wav("a.wav", Pattern(8, 0)), Wav("b.wav", Pattern(8, 0), Fmt(48000))], output));
  }

  [Fact]
  public void NonRiffIsRejected()
  {
    var path = Path.Combine(_root, "x.wav");
    File.WriteAllBytes(path, Pattern(100, 0));
    Assert.Throws<InvalidDataException>(() => WavJoiner.Join([path], Path.Combine(_root, "out.wav")));
  }

  [Theory]
  [InlineData(0xFFFFFFFFu)]
  [InlineData(0u)]
  public void UnknownDataSizeRunsToEndOfFile(uint declared)
  {
    var a = Pattern(200, 5);
    var b = Pattern(100, 50);
    var output = Path.Combine(_root, "out.wav");
    WavJoiner.Join([Wav("a.wav", a, dataSize: declared), Wav("b.wav", b)], output);
    AssertCanonical(File.ReadAllBytes(output), [.. a, .. b]);
  }
}
