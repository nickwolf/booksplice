using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Discovery;

namespace BookSplice.Core.Tests.Chapters;

public sealed class ChapterPlannerTests
{
  [Fact]
  public void Create_uses_exact_cumulative_microseconds_and_retains_provenance()
  {
    var files = new[] { File("01.mp3", 1.25m, "01 - Opening"), File("02.mp3", 2.75m, "02 - Middle") };
    var plan = new ChapterPlanner().Create(files, true);

    Assert.True(plan.IsValid);
    Assert.Equal(4_000_000, plan.TotalDurationMicroseconds);
    Assert.Equal((0, 1_250_000), (plan.Entries[0].StartMicroseconds, plan.Entries[0].EndMicroseconds));
    Assert.Equal((1_250_000, 4_000_000), (plan.Entries[1].StartMicroseconds, plan.Entries[1].EndMicroseconds));
    Assert.Equal("Opening", plan.Entries[0].Title);
    Assert.Equal("01.mp3", plan.Entries[0].SourceRelativePath);
    Assert.Equal("01 - Opening", plan.Entries[0].OriginalTitle);
  }

  [Fact]
  public void Create_uses_midpoint_to_even_for_fractional_seconds()
  {
    var plan = new ChapterPlanner().Create([File("a.mp3", 0.0000005m)], true);
    Assert.Equal(0, plan.Entries[0].EndMicroseconds);
  }

  [Fact]
  public void Create_disabled_does_not_require_durations()
  {
    var plan = new ChapterPlanner().Create([File("a.mp3", null)], false);
    Assert.True(plan.IsValid);
    Assert.Empty(plan.Entries);
  }

  [Fact]
  public void Create_rejects_missing_or_invalid_duration()
  {
    foreach (var duration in new decimal?[] { null, 0, -1 })
    {
      var plan = new ChapterPlanner().Create([File("a.mp3", duration)], true);
      Assert.False(plan.IsValid);
      Assert.NotNull(plan.ErrorCode);
    }
  }

  [Fact]
  public void Create_prefers_title_then_filename_and_uses_deterministic_numeric_fallback()
  {
    var plan = new ChapterPlanner().Create([
      File("a.mp3", 1, "2020 - A Year"),
      File("02.mp3", 1, "123"),
      File("03.mp3", 1)
    ], true);

    Assert.Equal("2020 - A Year", plan.Entries[0].Title);
    Assert.Equal("Chapter 2", plan.Entries[1].Title);
    Assert.Equal("Chapter 3", plan.Entries[2].Title);
  }

  [Fact]
  public void Create_preserves_repeated_titles_and_defensively_copies_input()
  {
    var files = new List<SourceFile> { File("01.mp3", 1, "Same"), File("02.mp3", 1, "Same") };
    var plan = new ChapterPlanner().Create(files, true);
    files.Clear();
    Assert.Equal(["Same", "Same"], plan.Entries.Select(entry => entry.Title));
  }

  private static SourceFile File(string relative, decimal? duration, string? title = null)
  {
    var tags = title is null ? [] : new[] { new KeyValuePair<string, string>("TITLE", title) };
    return new SourceFile(relative, relative, new MediaProbeResult([], [], [], new TagCollection(tags), new Dictionary<string, string>(), [], duration));
  }
}
