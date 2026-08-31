using AudiobookConverter.FFmpeg.Commands;

namespace AudiobookConverter.FFmpeg.Tests.Commands;

public sealed class FFmetadataWriterTests
{
  [Fact]
  public void WriteEscapesAllFfmetadataSpecialCharactersAndLineEndings()
  {
    var output = new FFmetadataWriter().Write(new Dictionary<string, string> { ["TITLE"] = "One\\Two=Three;Four#Five\r\nSix\nSeven\rEight" });
    Assert.Equal(";FFMETADATA1\nTITLE=One\\\\Two\\=Three\\;Four\\#Five\\\nSix\\\nSeven\\\nEight\n", output);
  }

  [Fact]
  public void WriteRetainsInputKeySpellingAndEmitsStableKeyOrder()
  {
    var output = new FFmetadataWriter().Write(new Dictionary<string, string> { ["z-custom"] = "last", ["ALBUM"] = "Book" });
    Assert.Equal(";FFMETADATA1\nALBUM=Book\nz-custom=last\n", output);
  }

  [Fact]
  public void Mp4MetadataWriterPlacesProfileCustomFieldsInITunesFreeformAtoms()
  {
    var plan = Mp4MetadataWriter.CreatePlan(new Dictionary<string, string> { ["TITLE"] = "Book", ["ALBUM"] = "Book", ["ARTIST"] = "Author", ["ALBUMARTIST"] = "Author", ["COMPOSER"] = "Narrator", ["SERIES"] = "Series", ["SERIES-PART"] = "2", ["ASIN"] = "B000000000", ["WWWAUDIOFILE"] = "https://example.invalid/pd/B000000000", ["PUBLISHER"] = "Publisher", ["ITUNESMEDIATYPE"] = "Audiobook" });
    Assert.Equal("Book", plan.NativeFields["TITLE"]);
    Assert.Equal("Series", plan.FreeformFields["SERIES"]);
    Assert.Equal("2", plan.FreeformFields["SERIES-PART"]);
    Assert.Equal("B000000000", plan.FreeformFields["ASIN"]);
    Assert.Equal("https://example.invalid/pd/B000000000", plan.FreeformFields["WWWAUDIOFILE"]);
    Assert.Equal("Publisher", plan.FreeformFields["PUBLISHER"]);
    Assert.Equal("Audiobook", plan.FreeformFields["ITUNESMEDIATYPE"]);
  }

  [Fact]
  public void Mp4MetadataWriterPreservesUnknownSpellingAndResolvesNativeCollisionsCaseInsensitively()
  {
    var plan = Mp4MetadataWriter.CreatePlan(new Dictionary<string, string> { ["album"] = "Mapped Book", ["ALBUM"] = "Profile Book", ["AUDIBLE_Custom"] = "Keep me" });
    Assert.Single(plan.NativeFields);
    Assert.Equal("Profile Book", plan.NativeFields["album"]);
    Assert.Equal("Keep me", plan.FreeformFields["AUDIBLE_Custom"]);
  }
}
