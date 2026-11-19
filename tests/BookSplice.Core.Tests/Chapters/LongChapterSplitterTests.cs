using BookSplice.Core.Chapters;

namespace BookSplice.Core.Tests.Chapters;

public sealed class LongChapterSplitterTests
{
  [Theory]
  [InlineData(44_100, 48_694_774_308)]
  [InlineData(48_000, 44_738_242_645)]
  [InlineData(32_000, 67_107_863_968)]
  public void MaximumMicroseconds_leaves_one_second_of_samples_below_the_32_bit_limit(int sampleRate, long expected)
    => Assert.Equal(expected, LongChapterSplitter.MaximumMicroseconds(sampleRate));

  [Fact]
  public void Split_returns_chapters_at_or_under_the_limit_untouched()
  {
    var limit = LongChapterSplitter.MaximumMicroseconds(44_100);
    var chapters = new[] { new ChapterEntry(0, limit, "One", "Orig", "a.mp3"), new ChapterEntry(limit, limit + 5, "Two", "Orig", "b.mp3") };
    var result = LongChapterSplitter.Split(chapters, 44_100);
    Assert.Same(chapters, result);
  }

  [Fact]
  public void Split_cuts_a_chapter_just_over_the_limit_into_two_equal_parts()
  {
    var limit = LongChapterSplitter.MaximumMicroseconds(44_100);
    var result = LongChapterSplitter.Split([new ChapterEntry(1_000, 1_000 + limit + 1, "Long", "Orig", "a.mp3")], 44_100);
    Assert.Equal(2, result.Count);
    Assert.Equal("Long (part 1 of 2)", result[0].Title);
    Assert.Equal("Long (part 2 of 2)", result[1].Title);
    Assert.Equal(1_000, result[0].StartMicroseconds);
    Assert.Equal(result[0].EndMicroseconds, result[1].StartMicroseconds);
    Assert.Equal(1_000 + limit + 1, result[1].EndMicroseconds);
    Assert.InRange(result[0].EndMicroseconds - result[0].StartMicroseconds, (limit + 1) / 2, (limit + 2) / 2);
    Assert.All(result, part => Assert.Equal(("Orig", "a.mp3"), (part.OriginalTitle, part.SourceRelativePath)));
  }

  [Fact]
  public void Split_makes_as_many_contiguous_parts_as_the_duration_needs_and_leaves_other_chapters_alone()
  {
    var limit = LongChapterSplitter.MaximumMicroseconds(48_000);
    var before = new ChapterEntry(0, 10, "Before", "Before", "a.mp3");
    var after = new ChapterEntry(10 + 2 * limit + 1, 20 + 2 * limit, "After", "After", "c.mp3");
    var result = LongChapterSplitter.Split([before, new ChapterEntry(10, 10 + 2 * limit + 1, "Long", "Long", "b.mp3"), after], 48_000);
    Assert.Equal(5, result.Count);
    Assert.Same(before, result[0]);
    Assert.Same(after, result[^1]);
    Assert.Equal(["Long (part 1 of 3)", "Long (part 2 of 3)", "Long (part 3 of 3)"], result.Skip(1).Take(3).Select(part => part.Title));
    Assert.Equal(10, result[1].StartMicroseconds);
    Assert.All(result.Zip(result.Skip(1)), pair => Assert.Equal(pair.First.EndMicroseconds, pair.Second.StartMicroseconds));
    Assert.All(result.Skip(1).Take(3), part => Assert.InRange(part.EndMicroseconds - part.StartMicroseconds, 1, limit));
  }

  [Fact]
  public void Split_rejects_a_non_positive_sample_rate()
    => Assert.Throws<ArgumentOutOfRangeException>(() => LongChapterSplitter.Split([], 0));
}
