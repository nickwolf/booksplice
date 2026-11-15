using BookSplice.Core.Chapters;

namespace BookSplice.Core.Tests.Chapters;

public sealed class ChapterMarksTests
{
  private const long Hundred = 360_000_000_000;
  private const long Second = 1_000_000;

  private static ChapterMark[] Three() => [new(0, "A"), new(10 * Second, "B"), new(20 * Second, "C")];

  private static ChapterMarkProblemCode[] Codes(IReadOnlyList<ChapterMark> marks, long total) => ChapterMarks.Validate(marks, total).Problems.Select(p => p.Code).ToArray();

  [Fact]
  public void Validate_accepts_a_well_formed_list()
  {
    var result = ChapterMarks.Validate(Three(), 30 * Second);
    Assert.True(result.IsValid);
    Assert.Empty(result.Problems);
  }

  [Fact]
  public void Validate_accepts_chapters_of_exactly_one_second()
    => Assert.True(ChapterMarks.Validate([new(0, "A"), new(Second, "B")], 2 * Second).IsValid);

  [Fact]
  public void Validate_rejects_an_empty_list_and_a_non_positive_total()
  {
    Assert.Contains(ChapterMarkProblemCode.NoChapters, Codes([], Second));
    Assert.Contains(ChapterMarkProblemCode.InvalidBookLength, Codes([new(0, "A")], 0));
    Assert.Contains(ChapterMarkProblemCode.InvalidBookLength, Codes([new(0, "A")], -5));
  }

  [Theory]
  [InlineData(1L)]
  [InlineData(-1L)]
  public void Validate_requires_the_first_mark_at_zero(long start)
  {
    var result = ChapterMarks.Validate([new(start, "A"), new(20 * Second, "B")], 30 * Second);
    var problem = Assert.Single(result.Problems, p => p.Code == ChapterMarkProblemCode.FirstNotAtZero);
    Assert.Equal(0, problem.Index);
    Assert.Equal("The first chapter must start at 0:00:00.000", problem.Message);
  }

  [Theory]
  [InlineData(10L)]
  [InlineData(5L)]
  public void Validate_requires_strictly_increasing_starts(long second)
  {
    var result = ChapterMarks.Validate([new(0, "A"), new(10 * Second, "B"), new(second * Second, "C")], 30 * Second);
    Assert.Contains(result.Problems, p => p.Index == 2 && p.Code == ChapterMarkProblemCode.NotIncreasing);
  }

  [Theory]
  [InlineData(30L)]
  [InlineData(31L)]
  public void Validate_rejects_a_mark_at_or_after_the_end(long second)
  {
    var result = ChapterMarks.Validate([new(0, "A"), new(second * Second, "B")], 30 * Second);
    var problem = Assert.Single(result.Problems);
    Assert.Equal((1, ChapterMarkProblemCode.AfterEnd), (problem.Index, problem.Code));
  }

  [Fact]
  public void Validate_rejects_chapters_shorter_than_one_second_including_the_last()
  {
    var middle = ChapterMarks.Validate([new(0, "A"), new(10 * Second, "B"), new(10 * Second + 999_999, "C")], 30 * Second);
    Assert.Equal((1, ChapterMarkProblemCode.TooShort), (middle.Problems[0].Index, middle.Problems[0].Code));
    Assert.Equal("Shorter than 1 second", middle.Problems[0].Message);
    var last = ChapterMarks.Validate([new(0, "A"), new(30 * Second - 999_999, "B")], 30 * Second);
    Assert.Equal((1, ChapterMarkProblemCode.TooShort), (last.Problems[0].Index, last.Problems[0].Code));
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void Validate_requires_a_non_blank_title(string title)
  {
    var result = ChapterMarks.Validate([new(0, title)], 5 * Second);
    Assert.Equal((0, ChapterMarkProblemCode.EmptyTitle), (result.Problems[0].Index, result.Problems[0].Code));
  }

  [Fact]
  public void Validate_reports_overflow_as_a_problem_instead_of_throwing()
  {
    var marks = new[] { new ChapterMark(0, "A"), new ChapterMark(long.MinValue, "B") };
    Assert.Contains(ChapterMarkProblemCode.NotIncreasing, Codes(marks, long.MaxValue));
    var wide = new[] { new ChapterMark(long.MinValue, "A") };
    var codes = Codes(wide, long.MaxValue);
    Assert.Contains(ChapterMarkProblemCode.FirstNotAtZero, codes);
    Assert.Contains(ChapterMarkProblemCode.Overflow, codes);
  }

  [Fact]
  public void Validate_is_exact_on_a_hundred_hour_book()
  {
    Assert.True(ChapterMarks.Validate([new(0, "A"), new(Hundred - Second, "B")], Hundred).IsValid);
    Assert.Contains(ChapterMarkProblemCode.TooShort, Codes([new(0, "A"), new(Hundred - Second + 1, "B")], Hundred));
    Assert.Contains(ChapterMarkProblemCode.AfterEnd, Codes([new(0, "A"), new(Hundred, "B")], Hundred));
    Assert.True(ChapterMarks.Validate([new(0, "A"), new(long.MaxValue - Second, "B")], long.MaxValue).IsValid);
  }

  [Fact]
  public void FromChapterPlan_seeds_one_mark_per_track_and_is_empty_for_an_invalid_plan()
  {
    var plan = ChapterPlan.Valid([new ChapterEntry(0, 5, "One", "01", "a.mp3"), new ChapterEntry(5, 9, "Two", "02", "b.mp3")], 9);
    Assert.Equal([new ChapterMark(0, "One"), new ChapterMark(5, "Two")], ChapterMarks.FromChapterPlan(plan));
    Assert.Empty(ChapterMarks.FromChapterPlan(ChapterPlan.Invalid("x", "y")));
  }

  [Fact]
  public void Split_inserts_a_mark_titled_with_a_suffix_and_leaves_the_input_alone()
  {
    var marks = Three();
    var result = ChapterMarks.Split(marks, 1, 15 * Second, 30 * Second);
    Assert.True(result.IsAccepted);
    Assert.Equal([new ChapterMark(0, "A"), new ChapterMark(10 * Second, "B"), new ChapterMark(15 * Second, "B (2)"), new ChapterMark(20 * Second, "C")], result.Marks);
    Assert.Equal(Three(), marks);
  }

  [Fact]
  public void Split_accepts_the_edges_that_leave_one_second_each_side()
  {
    Assert.True(ChapterMarks.Split(Three(), 1, 11 * Second, 30 * Second).IsAccepted);
    Assert.True(ChapterMarks.Split(Three(), 1, 19 * Second, 30 * Second).IsAccepted);
    Assert.True(ChapterMarks.Split(Three(), 2, 29 * Second, 30 * Second).IsAccepted);
  }

  [Theory]
  [InlineData(10_000_000L, ChapterMarkProblemCode.NotIncreasing)]
  [InlineData(10_999_999L, ChapterMarkProblemCode.TooShort)]
  [InlineData(19_000_001L, ChapterMarkProblemCode.TooShort)]
  [InlineData(20_000_000L, ChapterMarkProblemCode.NotIncreasing)]
  [InlineData(5_000_000L, ChapterMarkProblemCode.NotIncreasing)]
  public void Split_refuses_points_outside_the_chapter_or_too_close_to_an_edge(long at, ChapterMarkProblemCode expected)
  {
    var result = ChapterMarks.Split(Three(), 1, at, 30 * Second);
    Assert.False(result.IsAccepted);
    Assert.Null(result.Marks);
    Assert.Contains(expected, result.Problems.Select(p => p.Code));
  }

  [Fact]
  public void Split_of_the_last_chapter_cannot_pass_the_end_of_the_book()
  {
    var result = ChapterMarks.Split(Three(), 2, 30 * Second, 30 * Second);
    Assert.Contains(result.Problems, p => p.Code == ChapterMarkProblemCode.AfterEnd);
  }

  [Fact]
  public void Split_trims_the_title_before_adding_the_suffix()
    => Assert.Equal("A (2)", ChapterMarks.Split([new(0, " A ")], 0, 5 * Second, 10 * Second).Marks![1].Title);

  [Fact]
  public void Split_numbers_new_chapters_from_the_base_title_and_skips_taken_numbers()
  {
    var marks = ChapterMarks.Split([new(0, "Ch 1")], 0, 20 * Second, 100 * Second).Marks!;
    Assert.Equal("Ch 1 (2)", marks[1].Title);
    marks = ChapterMarks.Split(marks, 1, 40 * Second, 100 * Second).Marks!;
    Assert.Equal("Ch 1 (3)", marks[2].Title);
    marks = ChapterMarks.Split(marks, 0, 10 * Second, 100 * Second).Marks!;
    Assert.Equal("Ch 1 (4)", marks[1].Title);
    Assert.Equal("Ch 1 (5)", ChapterMarks.Split(marks, 3, 60 * Second, 100 * Second).Marks![4].Title);
  }

  [Fact]
  public void Split_only_strips_a_trailing_number_in_parentheses()
  {
    Assert.Equal("Part (x) (2)", ChapterMarks.Split([new(0, "Part (x)")], 0, 5 * Second, 10 * Second).Marks![1].Title);
    Assert.Equal("A (1) b (2)", ChapterMarks.Split([new(0, "A (1) b")], 0, 5 * Second, 10 * Second).Marks![1].Title);
    Assert.Equal("(7) (2)", ChapterMarks.Split([new(0, "(7)")], 0, 5 * Second, 10 * Second).Marks![1].Title);
  }

  [Fact]
  public void MergeWithNext_keeps_the_first_title_and_start()
  {
    var result = ChapterMarks.MergeWithNext(Three(), 0, 30 * Second);
    Assert.Equal([new ChapterMark(0, "A"), new ChapterMark(20 * Second, "C")], result.Marks);
  }

  [Fact]
  public void MergeWithNext_refuses_the_last_chapter()
  {
    var result = ChapterMarks.MergeWithNext(Three(), 2, 30 * Second);
    Assert.False(result.IsAccepted);
    Assert.Equal(ChapterMarkProblemCode.NoNextChapter, Assert.Single(result.Problems).Code);
  }

  [Fact]
  public void MergeWithNext_can_reduce_a_book_to_one_chapter()
  {
    var result = ChapterMarks.MergeWithNext([new(0, "A"), new(5 * Second, "B")], 0, 10 * Second);
    Assert.Equal([new ChapterMark(0, "A")], result.Marks);
  }

  [Fact]
  public void MoveStart_moves_a_boundary_with_microsecond_precision()
  {
    var result = ChapterMarks.MoveStart(Three(), 1, 12_345_678, 30 * Second);
    Assert.Equal(12_345_678, result.Marks![1].StartMicroseconds);
  }

  [Theory]
  [InlineData(1, 0L)]
  [InlineData(1, 20_000_000L)]
  [InlineData(1, 25_000_000L)]
  [InlineData(1, 19_500_000L)]
  [InlineData(0, 5_000_000L)]
  [InlineData(2, 30_000_000L)]
  [InlineData(2, 10_000_000L)]
  public void MoveStart_refuses_overlap_short_chapters_and_a_non_zero_first_start(int index, long start)
    => Assert.False(ChapterMarks.MoveStart(Three(), index, start, 30 * Second).IsAccepted);

  [Fact]
  public void Rename_trims_and_rejects_blank_titles()
  {
    Assert.Equal("New", ChapterMarks.Rename(Three(), 0, "  New ", 30 * Second).Marks![0].Title);
    var blank = ChapterMarks.Rename(Three(), 0, " ", 30 * Second);
    Assert.Equal(ChapterMarkProblemCode.EmptyTitle, Assert.Single(blank.Problems).Code);
  }

  [Fact]
  public void Operations_reject_an_index_outside_the_list()
  {
    foreach (var index in new[] { -1, 3 })
    {
      Assert.Equal(ChapterMarkProblemCode.IndexOutOfRange, Assert.Single(ChapterMarks.Split(Three(), index, 5 * Second, 30 * Second).Problems).Code);
      Assert.Equal(ChapterMarkProblemCode.IndexOutOfRange, Assert.Single(ChapterMarks.MergeWithNext(Three(), index, 30 * Second).Problems).Code);
      Assert.Equal(ChapterMarkProblemCode.IndexOutOfRange, Assert.Single(ChapterMarks.MoveStart(Three(), index, 5 * Second, 30 * Second).Problems).Code);
      Assert.Equal(ChapterMarkProblemCode.IndexOutOfRange, Assert.Single(ChapterMarks.Rename(Three(), index, "x", 30 * Second).Problems).Code);
    }
  }

  [Fact]
  public void Operations_are_exact_on_a_hundred_hour_book()
  {
    var marks = new[] { new ChapterMark(0, "A") };
    var split = ChapterMarks.Split(marks, 0, Hundred - Second, Hundred);
    Assert.Equal(Hundred - Second, split.Marks![1].StartMicroseconds);
    Assert.False(ChapterMarks.Split(marks, 0, Hundred - Second + 1, Hundred).IsAccepted);
    Assert.False(ChapterMarks.Split(marks, 0, long.MaxValue, Hundred).IsAccepted);
  }

  [Fact]
  public void Operations_near_long_max_do_not_throw()
  {
    var marks = new[] { new ChapterMark(0, "A") };
    Assert.True(ChapterMarks.Split(marks, 0, long.MaxValue - Second, long.MaxValue).IsAccepted);
    Assert.False(ChapterMarks.Split(marks, 0, long.MinValue, long.MaxValue).IsAccepted);
  }

  [Fact]
  public void ToEntries_maps_marks_to_the_track_that_contains_their_start()
  {
    var tracks = ChapterPlan.Valid([new ChapterEntry(0, 10 * Second, "One", "01 One", "a.mp3"), new ChapterEntry(10 * Second, 30 * Second, "Two", "02 Two", "b.mp3")], 30 * Second);
    var entries = ChapterMarks.ToEntries([new(0, "A"), new(5 * Second, " B "), new(10 * Second, "C"), new(25 * Second, "D")], tracks);
    Assert.Equal([
      new ChapterEntry(0, 5 * Second, "A", "01 One", "a.mp3"),
      new ChapterEntry(5 * Second, 10 * Second, "B", "01 One", "a.mp3"),
      new ChapterEntry(10 * Second, 25 * Second, "C", "02 Two", "b.mp3"),
      new ChapterEntry(25 * Second, 30 * Second, "D", "02 Two", "b.mp3")], entries);
  }

  [Fact]
  public void ToEntries_refuses_invalid_marks()
  {
    var tracks = ChapterPlan.Valid([new ChapterEntry(0, 10 * Second, "One", "One", "a.mp3")], 10 * Second);
    Assert.Throws<ArgumentException>(() => ChapterMarks.ToEntries([new(Second, "A")], tracks));
  }
}

public sealed class ChapterTimeTests
{
  [Theory]
  [InlineData(0L, "0:00:00.000")]
  [InlineData(1_000L, "0:00:00.001")]
  [InlineData(1_234_567L, "0:00:01.234567")]
  [InlineData(61_500_000L, "0:01:01.500")]
  [InlineData(3_661_001_000L, "1:01:01.001")]
  [InlineData(360_000_000_000L, "100:00:00.000")]
  public void Format_writes_hours_minutes_seconds_and_milliseconds(long microseconds, string expected)
    => Assert.Equal(expected, ChapterTime.Format(microseconds));

  [Fact]
  public void Format_refuses_negative_values()
    => Assert.Throws<ArgumentOutOfRangeException>(() => ChapterTime.Format(-1));

  [Theory]
  [InlineData(0L)]
  [InlineData(1L)]
  [InlineData(999_999L)]
  [InlineData(1_000_000L)]
  [InlineData(59_999_999L)]
  [InlineData(3_599_999_999L)]
  [InlineData(360_000_000_000L)]
  [InlineData(360_000_000_001L)]
  public void Format_then_TryParse_round_trips(long microseconds)
  {
    Assert.True(ChapterTime.TryParse(ChapterTime.Format(microseconds), out var parsed));
    Assert.Equal(microseconds, parsed);
  }

  [Theory]
  [InlineData("1:02:03.456", 3_723_456_000L)]
  [InlineData("1:02:03", 3_723_000_000L)]
  [InlineData("2:03", 123_000_000L)]
  [InlineData("2:03.5", 123_500_000L)]
  [InlineData("12.25", 12_250_000L)]
  [InlineData("7", 7_000_000L)]
  [InlineData("90", 90_000_000L)]
  [InlineData("100:00:00.000", 360_000_000_000L)]
  [InlineData(" 0:00:01.000001 ", 1_000_001L)]
  [InlineData("0:00:00.1", 100_000L)]
  public void TryParse_accepts_the_documented_forms(string text, long expected)
  {
    Assert.True(ChapterTime.TryParse(text, out var parsed));
    Assert.Equal(expected, parsed);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("  ")]
  [InlineData("abc")]
  [InlineData("-1")]
  [InlineData("-0:01")]
  [InlineData("+1")]
  [InlineData("1:2")]
  [InlineData("1:60")]
  [InlineData("1:60:00")]
  [InlineData("1:00:60")]
  [InlineData("1:2:3:4")]
  [InlineData(":30")]
  [InlineData("1:")]
  [InlineData("1.")]
  [InlineData(".5")]
  [InlineData("1.1234567")]
  [InlineData("1.2.3")]
  [InlineData("1 :00")]
  [InlineData("1e3")]
  [InlineData("0x10")]
  [InlineData("99999999999999999999")]
  [InlineData("999999999999999:00:00")]
  [InlineData("٣:00")]
  public void TryParse_rejects_garbage(string? text)
  {
    Assert.False(ChapterTime.TryParse(text, out var parsed));
    Assert.Equal(0, parsed);
  }
}
