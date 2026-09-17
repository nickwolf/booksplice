using BookSplice.FFmpeg.Commands;

namespace BookSplice.FFmpeg.Tests.Commands;

public sealed class ConcatManifestWriterTests
{
  [Fact]
  public void WriteEscapesApostrophesAndKeepsUnicodePaths()
  {
    var manifest = ConcatManifestWriter.Write([@"C:\audio\L'été 你好.m4a"]);
    Assert.Equal("file 'C:/audio/L\\'été 你好.m4a'\n", manifest);
  }

  [Theory]
  [InlineData("C:\\audio\\bad\nname.m4a")]
  [InlineData("C:\\audio\\bad\0name.m4a")]
  public void WriteRejectsUnsafePaths(string path)
    => Assert.Throws<ArgumentException>(() => ConcatManifestWriter.Write([path]));
}
