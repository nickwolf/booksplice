using AudiobookConverter.Core.Analysis;
using AudiobookConverter.Core.Discovery;
using AudiobookConverter.Core.Metadata;

namespace AudiobookConverter.Core.Tests.Metadata;

public sealed class MetadataAggregatorTests
{
  private readonly MetadataAggregator _aggregator = new();

  [Fact]
  public void Aggregate_marks_normalized_equal_album_values_as_consistent_and_keeps_first_spelling()
  {
    var metadata = _aggregator.Aggregate([
      File("part-01.m4a", ("ALBUM", "  Caf\u00e9\u00a0Book  ")),
      File("part-02.m4a", ("album", "Cafe\u0301 Book"))
    ]);

    var title = metadata.Get(SemanticField.BookTitle);

    Assert.Equal(AggregationState.Consistent, title.State);
    Assert.Equal("  Caf\u00e9\u00a0Book  ", title.Value);
    Assert.Equal("part-01.m4a", title.Candidates[0].SourceFile);
    Assert.Equal("ALBUM", title.Candidates[0].SourceKey);
  }

  [Fact]
  public void Aggregate_reports_missing_partial_and_conflicting_values()
  {
    var metadata = _aggregator.Aggregate([
      File("one.m4a", ("ALBUMARTIST", "Author"), ("COMPOSER", "Reader One")),
      File("two.m4a", ("ALBUMARTIST", "Author"), ("COMPOSER", "Reader Two")),
      File("three.m4a", ("ALBUMARTIST", "Author"))
    ]);

    Assert.Equal(AggregationState.Consistent, metadata.Get(SemanticField.Author).State);
    Assert.Equal(AggregationState.Conflicting, metadata.Get(SemanticField.Narrator).State);
    Assert.Equal(AggregationState.Missing, metadata.Get(SemanticField.Series).State);

    var partial = _aggregator.Aggregate([File("one.m4a", ("PUBLISHER", "Publisher")), File("two.m4a")]);
    Assert.Equal(AggregationState.PartiallyMissing, partial.Get(SemanticField.Publisher).State);
    Assert.Equal("Publisher", partial.Get(SemanticField.Publisher).Value);
  }

  [Fact]
  public void Aggregate_uses_shared_title_when_album_is_not_consistent_and_ignores_track_fields()
  {
    var metadata = _aggregator.Aggregate([
      File("one.m4a", ("ALBUM", "Disc One"), ("TITLE", "Chapter One"), ("TRACK", "1"), ("DISC", "1")),
      File("two.m4a", ("ALBUM", "Disc Two"), ("TITLE", "Chapter One"), ("TRACK", "2"), ("DISC", "2"))
    ]);

    var title = metadata.Get(SemanticField.BookTitle);

    Assert.Equal(AggregationState.Consistent, title.State);
    Assert.Equal("Chapter One", title.Value);
    Assert.Equal("TITLE", title.Candidates[0].SourceKey);
    Assert.DoesNotContain(SemanticField.Track, metadata.Fields.Keys);
    Assert.DoesNotContain(SemanticField.Disc, metadata.Fields.Keys);
  }

  [Fact]
  public void Aggregate_uses_source_root_when_no_credible_book_title_exists()
  {
    var metadata = _aggregator.Aggregate([
      File("C:\\Books\\Fallback Book\\one.m4a", "one.m4a", ("TITLE", "Chapter One")),
      File("C:\\Books\\Fallback Book\\two.m4a", "two.m4a", ("TITLE", "Chapter Two"))
    ]);

    var title = metadata.Get(SemanticField.BookTitle);

    Assert.Equal(AggregationState.Consistent, title.State);
    Assert.Equal("Fallback Book", title.Value);
    Assert.Equal("source-root", title.Candidates[0].SourceKey);
  }

  [Fact]
  public void Aggregate_uses_album_artist_before_artist_and_never_infers_narrator_from_artist()
  {
    var metadata = _aggregator.Aggregate([
      File("one.m4a", ("ALBUMARTIST", "Primary Author"), ("ARTIST", "Primary Author; Narrator")),
      File("two.m4a", ("ALBUMARTIST", "Primary Author"), ("ARTIST", "Primary Author; Narrator"))
    ]);

    Assert.Equal("Primary Author", metadata.Get(SemanticField.Author).Value);
    Assert.Equal(AggregationState.Missing, metadata.Get(SemanticField.Narrator).State);
  }

  [Fact]
  public void NickMp3tag_profile_maps_canonical_fields_and_preserves_unknown_tag_spelling()
  {
    var metadata = _aggregator.Aggregate([
      File("one.m4a", ("ALBUM", "Book"), ("ALBUMARTIST", "Author"), ("COMPOSER", "Narrator"), ("SERIES", "Series"), ("SERIES-PART", "2"), ("ASIN", "B000000000"), ("AUDIBLE_Custom", "Keep me")),
      File("two.m4a", ("ALBUM", "Book"), ("ALBUMARTIST", "Author"), ("COMPOSER", "Narrator"), ("SERIES", "Series"), ("SERIES-PART", "2"), ("ASIN", "B000000000"), ("AUDIBLE_Custom", "Keep me"))
    ]);

    var tags = MetadataProfiles.NickMp3tag.Apply(metadata);

    Assert.Equal("Book", tags["TITLE"]);
    Assert.Equal("Book", tags["ALBUM"]);
    Assert.Equal("Author", tags["ARTIST"]);
    Assert.Equal("Author", tags["ALBUMARTIST"]);
    Assert.Equal("Narrator", tags["COMPOSER"]);
    Assert.Equal("Series", tags["SERIES"]);
    Assert.Equal("2", tags["SERIES-PART"]);
    Assert.Equal("B000000000", tags["ASIN"]);
    Assert.Equal("Keep me", tags["AUDIBLE_Custom"]);
    Assert.False(tags.ContainsKey("SERIESPART"));
    Assert.False(tags.ContainsKey("DISCNUMBER"));
  }

  [Fact]
  public void AggregateKeepsConflictingAlbumStateWhenTitleIsUnusable()
  {
    var metadata = _aggregator.Aggregate([File("C:\\Books\\Fallback Book\\one.m4a", "one.m4a", ("ALBUM", "First"), ("TITLE", "Chapter One")), File("C:\\Books\\Fallback Book\\two.m4a", "two.m4a", ("ALBUM", "Second"), ("TITLE", "Chapter Two"))]);
    var title = metadata.Get(SemanticField.BookTitle);
    Assert.Equal(AggregationState.Conflicting, title.State); Assert.Equal("Fallback Book", title.Value); Assert.Equal(3, title.Candidates.Count);
  }

  [Fact]
  public void AggregateKeepsConflictingAlbumStateWhenSharedTitleIsProvisional()
  {
    var metadata = _aggregator.Aggregate([File("one.m4a", ("ALBUM", "First"), ("TITLE", "Shared")), File("two.m4a", ("ALBUM", "Second"), ("TITLE", "Shared"))]);
    var title = metadata.Get(SemanticField.BookTitle);
    Assert.Equal(AggregationState.Conflicting, title.State); Assert.Equal("Shared", title.Value); Assert.Equal(4, title.Candidates.Count);
  }

  [Fact]
  public void AggregateSelectsPartialAlbumWithoutErasingItsState()
  {
    var metadata = _aggregator.Aggregate([File("one.m4a", ("ALBUM", "Book")), File("two.m4a")]);
    var title = metadata.Get(SemanticField.BookTitle);
    Assert.Equal(AggregationState.PartiallyMissing, title.State); Assert.Equal("Book", title.Value); Assert.Single(title.Candidates);
  }
  private static SourceFile File(string relativePath, params (string Key, string Value)[] tags)
    => File(relativePath, relativePath, tags);

  private static SourceFile File(string fullPath, string relativePath, params (string Key, string Value)[] tags)
    => new(fullPath, relativePath, new MediaProbeResult([], [], [], new TagCollection(tags.Select(tag => new KeyValuePair<string, string>(tag.Key, tag.Value))), new Dictionary<string, string>(tags.ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal)), [], 1));
}
