using BookSplice.Core.Analysis;
using BookSplice.Core.EditCopy;
using BookSplice.Core.Metadata;

namespace BookSplice.Core.Tests.EditCopy;

public sealed class EditCopyPreviewTests
{
  [Fact]
  public void EachChangeGetsOneLineWithTheOldAndNewValues()
  {
    var original = Original(new() { ["title"] = "Old title", ["comment"] = "Old comment" }, "Opening", "Middle", "Ending");
    var request = new EditCopyRequest(
      @"C:\Books\Original.m4b",
      new Dictionary<SemanticField, string> { [SemanticField.BookTitle] = "New title", [SemanticField.Genre] = "Fantasy", [SemanticField.Comment] = "" },
      new Dictionary<int, string> { [2] = "The end" },
      @"C:\Output\Edited.m4b",
      MetadataProfiles.GenericMp4);

    var lines = EditCopyPreview.Describe(original, Chapters("Opening", "Middle", "Ending"), request);

    Assert.Equal(
    [
      "Title: \"Old title\" -> \"New title\"",
      "Genre: (none) -> \"Fantasy\"",
      "Comment: \"Old comment\" -> (removed)",
      "Chapter 3: \"Ending\" -> \"The end\"",
    ], lines);
  }

  [Fact]
  public void TheOldValueComesFromAnyKeyTheProfileMapsTheFieldTo()
  {
    var original = Original(new() { ["album_artist"] = "Old author" }, "Opening");
    var request = new EditCopyRequest(@"C:\Books\Original.m4b", new Dictionary<SemanticField, string> { [SemanticField.Author] = "New author" }, new Dictionary<int, string>(), @"C:\Output\Edited.m4b", MetadataProfiles.GenericMp4);

    Assert.Equal(["Author: \"Old author\" -> \"New author\""], EditCopyPreview.Describe(original, Chapters("Opening"), request));
  }

  [Fact]
  public void TheOldChapterTitleComesFromTheChapterListNotTheProbe()
  {
    var whole = new string('x', 300);
    var original = Original(new(), whole[..255]);
    var request = new EditCopyRequest(@"C:\Books\Original.m4b", new Dictionary<SemanticField, string>(), new Dictionary<int, string> { [0] = "Short" }, @"C:\Output\Edited.m4b", MetadataProfiles.GenericMp4);

    Assert.Equal([$"Chapter 1: \"{whole}\" -> \"Short\""], EditCopyPreview.Describe(original, Chapters(whole), request));
  }

  [Theory]
  [InlineData(SemanticField.BookTitle, "Title")]
  [InlineData(SemanticField.ItunesMediaType, "iTunes media type")]
  [InlineData(SemanticField.SeriesPart, "Series part")]
  public void FieldLabelsAreReadable(SemanticField field, string label)
    => Assert.Equal(label, EditCopyPreview.Label(field));

  private static EditCopyChapter[] Chapters(params string[] titles) => titles.Select((title, index) => new EditCopyChapter(index * 10_000_000L, title)).ToArray();

  private static MediaProbeResult Original(Dictionary<string, string> tags, params string[] chapterTitles)
    => new(
      [new AudioTrack(0, "aac", "audio", 1, 44100, 60m, new Rational(1, 44100), new Dictionary<string, string>())],
      [],
      chapterTitles.Select((title, index) => new MediaChapter(index, index * 10m, (index + 1) * 10m, new Rational(1, 1000), new Dictionary<string, string> { ["title"] = $"probe {title}" })).ToArray(),
      new TagCollection(tags),
      tags,
      [],
      60m);
}
