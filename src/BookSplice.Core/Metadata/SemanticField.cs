namespace BookSplice.Core.Metadata;

public enum SemanticField
{
  BookTitle,
  Author,
  Narrator,
  Subtitle,
  AlbumSort,
  Series,
  SeriesPart,
  Genre,
  Year,
  ReleaseTime,
  Comment,
  Description,
  Publisher,
  Copyright,
  Asin,
  AudioFileUrl,
  Isbn,
  Language,
  ItunesMediaType,
  RatingWmp,
  ContentGroup,
  MovementName,
  Movement,
  ItunesGapless,
  AudibleAsin,
  AudibleAlbumArtistId,
  AudibleAcr,
  AudibleLocale,
  Format,
  Explicit,
  Rating,
  Track,
  Disc
}

public enum AggregationState
{
  Missing,
  Consistent,
  PartiallyMissing,
  Conflicting
}
