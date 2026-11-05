using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Metadata;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class DurationTextTests
{
  [Theory]
  [InlineData(0, "0:00:00")]
  [InlineData(999_999, "0:00:00")]
  [InlineData(1_000_000, "0:00:01")]
  [InlineData(3_599_000_000, "0:59:59")]
  [InlineData(3_600_000_000, "1:00:00")]
  [InlineData(33_561_319_999, "9:19:21")]
  [InlineData(90_000_000_000, "25:00:00")]
  [InlineData(-5_000_000, "0:00:00")]
  public void FormatsWholeSecondsAsHoursMinutesSeconds(long microseconds, string expected)
    => Assert.Equal(expected, DurationText.Format(TimeSpan.FromMicroseconds(microseconds)));

  [Fact]
  public void ChapterStartUsesTheSameFormat()
    => Assert.Equal("9:19:21", new ChapterViewModel(new ChapterEntry(33_561_319_999, 34_000_000_000, "One", "One", "01.mp3")).Start);

  [Fact]
  public void BookSummaryShowsTheDurationAsHoursMinutesSeconds()
  {
    var item = new BookQueueItemViewModel("book", AppSettings.Defaults);
    var metadata = new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
    item.SetAnalysis(new BookAnalysis(BookAnalysisStatus.Ready, "book", [], null, metadata, new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([], 33_561_319_999), []));
    Assert.Equal("0 files | 9:19:21 | ", item.Summary);
  }
}
