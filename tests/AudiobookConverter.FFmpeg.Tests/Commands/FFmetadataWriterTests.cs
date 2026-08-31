using AudiobookConverter.FFmpeg.Commands;

namespace AudiobookConverter.FFmpeg.Tests.Commands;

public sealed class FFmetadataWriterTests
{
  [Fact]
  public void WriteEscapesAllFfmetadataSpecialCharactersAndLineEndings()
  {
    var output = new FFmetadataWriter().Write(new Dictionary<string, string>
    {
      ["TITLE"] = "One\\Two=Three;Four#Five\r\nSix\nSeven\rEight"
    });

    Assert.Equal(";FFMETADATA1\nTITLE=One\\\\Two\\=Three\\;Four\\#Five\\\nSix\\\nSeven\\\nEight\n", output);
  }

  [Fact]
  public void WriteRetainsInputKeySpellingAndEmitsStableKeyOrder()
  {
    var output = new FFmetadataWriter().Write(new Dictionary<string, string>
    {
      ["z-custom"] = "last",
      ["ALBUM"] = "Book"
    });

    Assert.Equal(";FFMETADATA1\nALBUM=Book\nz-custom=last\n", output);
  }
}
