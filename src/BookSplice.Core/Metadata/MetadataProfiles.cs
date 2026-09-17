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

  public static MetadataProfile NickMp3tag { get; } = new(
    "NickMp3tag",
    "1",
    [
      new(SemanticField.BookTitle, "TITLE", "ALBUM"),
      new(SemanticField.Author, "ARTIST", "ALBUMARTIST"),
      new(SemanticField.Narrator, "COMPOSER"),
      new(SemanticField.Subtitle, "SUBTITLE"),
      new(SemanticField.AlbumSort, "ALBUMSORT"),
      new(SemanticField.Series, "SERIES"),
      new(SemanticField.SeriesPart, "SERIES-PART"),
      new(SemanticField.Genre, "GENRE"),
      new(SemanticField.Year, "YEAR"),
      new(SemanticField.ReleaseTime, "RELEASETIME"),
      new(SemanticField.Comment, "COMMENT"),
      new(SemanticField.Description, "DESCRIPTION"),
      new(SemanticField.Publisher, "PUBLISHER"),
      new(SemanticField.Copyright, "COPYRIGHT"),
      new(SemanticField.Asin, "ASIN"),
      new(SemanticField.AudioFileUrl, "WWWAUDIOFILE"),
      new(SemanticField.Isbn, "ISBN"),
      new(SemanticField.Language, "LANGUAGE"),
      new(SemanticField.ItunesMediaType, "ITUNESMEDIATYPE"),
      new(SemanticField.RatingWmp, "RATING WMP"),
      new(SemanticField.ContentGroup, "CONTENTGROUP"),
      new(SemanticField.MovementName, "MOVEMENTNAME"),
      new(SemanticField.Movement, "MOVEMENT"),
      new(SemanticField.ItunesGapless, "ITUNESGAPLESS"),
      new(SemanticField.AudibleAsin, "AUDIBLE_ASIN"),
      new(SemanticField.AudibleAlbumArtistId, "AUDIBLE_ALBUMARTISTID"),
      new(SemanticField.AudibleAcr, "AUDIBLE_ACR"),
      new(SemanticField.AudibleLocale, "AUDIBLE_LOCALE"),
      new(SemanticField.Format, "FORMAT"),
      new(SemanticField.Explicit, "EXPLICIT"),
      new(SemanticField.Rating, "RATING")
    ]);
}
