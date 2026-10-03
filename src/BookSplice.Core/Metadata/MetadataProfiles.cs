namespace BookSplice.Core.Metadata;

public static class MetadataProfiles
{
  public static MetadataProfile GenericMp4 { get; } = new(
    "GenericMp4",
    "1",
    [
      new(SemanticField.BookTitle, "title", "album"),
      new(SemanticField.Author, "artist", "album_artist"),
      new(SemanticField.Narrator, "composer"),
      new(SemanticField.Genre, "genre"),
      new(SemanticField.Year, "date"),
      new(SemanticField.Comment, "comment"),
      new(SemanticField.Description, "description"),
      new(SemanticField.Copyright, "copyright"),
      new(SemanticField.Language, "language"),
      new(SemanticField.ItunesMediaType, "media_type")
    ]);
}
