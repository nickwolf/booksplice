using BookSplice.Core.EditCopy;
using BookSplice.Core.Metadata;

namespace BookSplice.Core.Tests.EditCopy;

public sealed class EditCopyTagMappingTests
{
  [Fact]
  public void EachEditGoesToEveryKeyTheProfileMapsItTo()
  {
    var tags = EditCopyTagMapping.Map(MetadataProfiles.GenericMp4, new Dictionary<SemanticField, string>
    {
      [SemanticField.BookTitle] = "New title",
      [SemanticField.Author] = "New author",
      [SemanticField.Narrator] = "New narrator",
    }, out var diagnostics);

    Assert.Empty(diagnostics);
    Assert.Equal(new Dictionary<string, string>
    {
      ["title"] = "New title",
      ["album"] = "New title",
      ["artist"] = "New author",
      ["album_artist"] = "New author",
      ["composer"] = "New narrator",
    }, tags);
  }

  [Fact]
  public void AnAlbumArtistKeyIsWrittenUnderItsCanonicalName()
  {
    var profile = new MetadataProfile("Custom", "1", [new(SemanticField.Author, "ALBUMARTIST")]);

    var tags = EditCopyTagMapping.Map(profile, new Dictionary<SemanticField, string> { [SemanticField.Author] = "Someone" }, out var diagnostics);

    Assert.Empty(diagnostics);
    Assert.Equal("Someone", Assert.Single(tags, tag => tag.Key == "album_artist").Value);
  }

  [Fact]
  public void AFieldTheProfileDoesNotMapIsRejected()
  {
    var tags = EditCopyTagMapping.Map(MetadataProfiles.GenericMp4, new Dictionary<SemanticField, string> { [SemanticField.Asin] = "B000000000" }, out var diagnostics);

    Assert.Equal(EditCopyCodes.TagNotInProfile, Assert.Single(diagnostics).Code);
    Assert.Empty(tags);
  }

  [Fact]
  public void TwoFieldsWritingDifferentValuesToOneKeyAreRejected()
  {
    var profile = new MetadataProfile("Shared", "1", [new(SemanticField.BookTitle, "title"), new(SemanticField.Subtitle, "title")]);

    EditCopyTagMapping.Map(profile, new Dictionary<SemanticField, string> { [SemanticField.BookTitle] = "One", [SemanticField.Subtitle] = "Two" }, out var diagnostics);

    Assert.Equal(EditCopyCodes.TagConflict, Assert.Single(diagnostics).Code);
  }

  [Fact]
  public void TwoFieldsWritingTheSameValueToOneKeyAreAccepted()
  {
    var profile = new MetadataProfile("Shared", "1", [new(SemanticField.BookTitle, "title"), new(SemanticField.Subtitle, "title")]);

    var tags = EditCopyTagMapping.Map(profile, new Dictionary<SemanticField, string> { [SemanticField.BookTitle] = "Same", [SemanticField.Subtitle] = "Same" }, out var diagnostics);

    Assert.Empty(diagnostics);
    Assert.Equal("Same", Assert.Single(tags).Value);
  }

  [Fact]
  public void AValueContainingNulIsRejected()
  {
    var tags = EditCopyTagMapping.Map(MetadataProfiles.GenericMp4, new Dictionary<SemanticField, string> { [SemanticField.Genre] = "Fan\0tasy" }, out var diagnostics);

    Assert.Equal(EditCopyCodes.TagValueInvalid, Assert.Single(diagnostics).Code);
    Assert.Empty(tags);
  }

  [Fact]
  public void AnEmptyValueIsKeptAsARemoval()
  {
    var tags = EditCopyTagMapping.Map(MetadataProfiles.GenericMp4, new Dictionary<SemanticField, string> { [SemanticField.Comment] = "" }, out var diagnostics);

    Assert.Empty(diagnostics);
    Assert.Equal("", tags["comment"]);
  }
}
