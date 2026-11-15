using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Queue;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class ChapterMarkEditTests
{
  private const long Second = 1_000_000;
  private static readonly string Root = Path.GetFullPath("marks-book");

  private static BookAnalysis Analysis(int seconds = 100, params string[] tracks)
  {
    if (tracks.Length == 0) tracks = ["a.mp3", "b.mp3"];
    var each = seconds * Second / tracks.Length;
    var entries = tracks.Select((name, index) => new ChapterEntry(index * each, (index + 1) * each, $"Chapter {index + 1}", $"Chapter {index + 1}", name)).ToArray();
    var metadata = new BookMetadata(new Dictionary<SemanticField, AggregatedValue>
    {
      [SemanticField.BookTitle] = new(SemanticField.BookTitle, AggregationState.Consistent, "Book", []),
    }, new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
    var files = tracks.Select(name => new SourceFile(Path.Combine(Root, name), name,
      new MediaProbeResult([new AudioTrack(0, "mp3", "audio", 1, 44100, 1m, new Rational(1, 44100), new Dictionary<string, string>())], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1m),
      new SourceIdentity(Path.Combine(Root, name), 100, DateTime.UnixEpoch, name)));
    return new BookAnalysis(BookAnalysisStatus.Ready, Root, "book", files, null, metadata, new CoverDiscoveryResult([], [], null), ChapterPlan.Valid(entries, entries[^1].EndMicroseconds), [], [], tracks);
  }

  private static BookQueueItemViewModel Book(BookAnalysis? analysis = null)
  {
    var item = new BookQueueItemViewModel(Root, AppSettings.Defaults);
    item.SetAnalysis(analysis ?? Analysis());
    return item;
  }

  [Fact]
  public void AnUneditedBookShowsOneRowPerTrackAndNoEdits()
  {
    var item = Book();

    Assert.False(item.HasChapterEdits);
    Assert.False(item.CanUndoChapters);
    Assert.Equal(["0:00:00.000", "0:00:50.000"], item.ChapterRows.Select(row => row.Start));
    Assert.Equal(["0:00:50.000", "0:01:40.000"], item.ChapterRows.Select(row => row.End));
    Assert.Equal(["0:00:50.000", "0:00:50.000"], item.ChapterRows.Select(row => row.Length));
    Assert.Equal(["a.mp3", "b.mp3"], item.ChapterRows.Select(row => row.Source));
    Assert.Null(item.CreateOptions().ChapterMarks);
  }

  [Fact]
  public void ATitleEditOnAnUneditedBookKeepsTheTitlePathAndSeedsNothing()
  {
    var item = Book();

    var result = item.Rename(1, "Second");

    Assert.True(result.IsAccepted);
    Assert.False(item.HasChapterEdits);
    Assert.False(item.CanUndoChapters);
    Assert.Equal("Second", item.Chapters[1].Title);
    Assert.Equal("Second", item.ChapterRows[1].Title);
    Assert.Null(item.CreateOptions().ChapterMarks);
    Assert.Equal("Second", item.CreateOptions().ChapterTitles["b.mp3"]);
    Assert.False(item.Rename(5, "x").IsAccepted);
  }

  [Fact]
  public void TheFirstSplitSeedsMarksFromTheTracksWithTheTitleEditsAndTheOptionsCarryThem()
  {
    var item = Book();
    item.Chapters[0].Title = "Opening";

    var result = item.Split(0, 20 * Second);

    Assert.True(result.IsAccepted);
    Assert.True(item.HasChapterEdits);
    Assert.Equal(["Opening", "Opening (2)", "Chapter 2"], item.ChapterRows.Select(row => row.Title));
    Assert.Equal(["a.mp3", "a.mp3", "b.mp3"], item.ChapterRows.Select(row => row.Source));
    Assert.Equal(["0:00:20.000", "0:00:50.000", "0:01:40.000"], item.ChapterRows.Select(row => row.End));
    Assert.Equal([0, 20 * Second, 50 * Second], item.CreateOptions().ChapterMarks!.Select(mark => mark.StartMicroseconds));
    Assert.Equal("Opening (2)", item.CreateOptions().ChapterMarks![1].Title);
  }

  [Fact]
  public void SplitMergeMoveRenameAndUndoWalkBackToTheUneditedBook()
  {
    var item = Book();
    Assert.True(item.Split(0, 20 * Second).IsAccepted);
    Assert.True(item.MoveStart(1, 25 * Second).IsAccepted);
    Assert.True(item.Rename(1, "Middle").IsAccepted);
    Assert.True(item.MergeWithNext(1).IsAccepted);
    Assert.Equal(["Chapter 1", "Middle"], item.ChapterRows.Select(row => row.Title));
    Assert.Equal([0, 25 * Second], item.CreateOptions().ChapterMarks!.Select(mark => mark.StartMicroseconds));
    Assert.True(item.CanUndoChapters);

    Assert.True(item.UndoChapters());
    Assert.Equal(["Chapter 1", "Middle", "Chapter 2"], item.ChapterRows.Select(row => row.Title));
    Assert.True(item.UndoChapters());
    Assert.Equal(["Chapter 1", "Chapter 1 (2)", "Chapter 2"], item.ChapterRows.Select(row => row.Title));
    Assert.Equal("0:00:25.000", item.ChapterRows[1].Start);
    Assert.True(item.UndoChapters());
    Assert.Equal("0:00:20.000", item.ChapterRows[1].Start);
    Assert.True(item.UndoChapters());
    Assert.False(item.HasChapterEdits);
    Assert.Null(item.CreateOptions().ChapterMarks);
    Assert.Equal(2, item.ChapterRows.Count);
    Assert.False(item.CanUndoChapters);
    Assert.False(item.UndoChapters());
  }

  [Fact]
  public void AResetPushesUndoAndUndoBringsTheEditsBack()
  {
    var item = Book();
    Assert.False(item.ResetToTracks());
    item.Split(0, 20 * Second);

    Assert.True(item.ResetToTracks());
    Assert.False(item.HasChapterEdits);
    Assert.Equal(2, item.ChapterRows.Count);
    Assert.True(item.UndoChapters());
    Assert.Equal(3, item.ChapterRows.Count);
    Assert.True(item.HasChapterEdits);
  }

  [Fact]
  public void ARejectedEditChangesNothingAndPushesNothing()
  {
    var item = Book();
    var rejected = item.Split(0, 50 * Second);

    Assert.False(rejected.IsAccepted);
    Assert.NotEmpty(rejected.Problems);
    Assert.False(item.HasChapterEdits);
    Assert.False(item.CanUndoChapters);

    item.Split(0, 20 * Second);
    var before = item.ChapterRows;
    Assert.False(item.MoveStart(1, 100 * Second).IsAccepted);
    Assert.False(item.MergeWithNext(2).IsAccepted);
    Assert.False(item.MoveStart(0, Second).IsAccepted);
    Assert.False(item.Rename(1, "  ").IsAccepted);
    Assert.Equal(before, item.ChapterRows);
    Assert.True(item.UndoChapters());
    Assert.False(item.HasChapterEdits);
    Assert.False(item.CanUndoChapters);
  }

  [Fact]
  public void AFinishedOrBusyBookCannotBeEdited()
  {
    var busy = Book();
    busy.IsBusy = true;
    Assert.False(busy.Split(0, 20 * Second).IsAccepted);
    var done = Book();
    done.Status = "Complete";
    Assert.False(done.Split(0, 20 * Second).IsAccepted);
    Assert.False(new BookQueueItemViewModel(Root, AppSettings.Defaults).Split(0, Second).IsAccepted);
  }

  [Fact]
  public void MarksThatFailValidationMakeTheBookNotReadyWithTheFirstProblem()
  {
    var item = Book();
    Assert.True(item.CanConvert);
    var saved = new QueueItemRecord(Root, SavedItemSettings.From(AppSettings.Defaults),
      ChapterMarks: [new SavedChapterMark(0, "One"), new SavedChapterMark(100 * Second - 500_000, "Two")],
      ChapterMarksTotalMicroseconds: 100 * Second, ChapterMarksSources: ["a.mp3", "b.mp3"]);
    var restored = QueueSnapshotMapper.Build(saved, AppSettings.Defaults, null, out _)!;
    restored.SetAnalysis(Analysis());

    Assert.True(restored.HasChapterEdits);
    Assert.False(restored.CanConvert);
    Assert.Equal("Chapter 2: Shorter than 1 second.", restored.ChapterProblem);
    Assert.Equal("Shorter than 1 second.", restored.ChapterRows[1].Problem);
    Assert.Null(restored.ChapterRows[0].Problem);
    Assert.Equal("Fix chapters", restored.QueueStatus);
    Assert.True(restored.MoveStart(1, 60 * Second).IsAccepted);
    Assert.Null(restored.ChapterProblem);
    Assert.True(restored.CanConvert);
  }

  public static TheoryData<string> Triggers => new() { "reorder", "exclude", "add", "length" };

  [Theory]
  [MemberData(nameof(Triggers))]
  public void ChangingTheTracksOrTheTotalClearsMarksHistoryAndNotesIt(string trigger)
  {
    var item = Book();
    item.Split(0, 20 * Second);
    Assert.True(item.CanUndoChapters);

    item.SetAnalysis(trigger switch
    {
      "reorder" => Analysis(100, "b.mp3", "a.mp3"),
      "exclude" => Analysis(100, "a.mp3"),
      "add" => Analysis(100, "a.mp3", "b.mp3", "c.mp3"),
      _ => Analysis(101),
    });

    Assert.False(item.HasChapterEdits);
    Assert.False(item.CanUndoChapters);
    Assert.Null(item.CreateOptions().ChapterMarks);
    Assert.Contains(BookQueueItemViewModel.ChapterEditsClearedNote, item.Details, StringComparison.Ordinal);
  }

  [Fact]
  public void ANeedsDecisionAnalysisClearsMarks()
  {
    var item = Book();
    item.Split(0, 20 * Second);
    var undecided = new BookAnalysis(BookAnalysisStatus.NeedsDecision, Root, "book", [], null, Analysis().BookMetadata, new CoverDiscoveryResult([], [], null), ChapterPlan.Invalid("x", "y"), []);

    item.SetAnalysis(undecided);

    Assert.False(item.HasChapterEdits);
    Assert.Contains(BookQueueItemViewModel.ChapterEditsClearedNote, item.Details, StringComparison.Ordinal);
  }

  [Fact]
  public void AnIdenticalReanalysisKeepsMarksAndHistoryWithoutANote()
  {
    var item = Book();
    item.Split(0, 20 * Second);

    item.SetAnalysis(Analysis());

    Assert.True(item.HasChapterEdits);
    Assert.True(item.CanUndoChapters);
    Assert.Equal(3, item.CreateOptions().ChapterMarks!.Count);
    Assert.DoesNotContain(BookQueueItemViewModel.ChapterEditsClearedNote, item.Details, StringComparison.Ordinal);
  }

  [Fact]
  public void ATrackChangeWithoutMarksAddsNoNote()
  {
    var item = Book();
    item.SetAnalysis(Analysis(100, "a.mp3"));
    Assert.DoesNotContain(BookQueueItemViewModel.ChapterEditsClearedNote, item.Details, StringComparison.Ordinal);
  }

  [Fact]
  public void MarksSurviveTheSnapshotWhenTheTracksAreTheSame()
  {
    var item = Book();
    item.Split(0, 20 * Second);
    item.Rename(2, "Last");

    var record = QueueSnapshotMapper.Capture(item);

    Assert.Equal([0, 20 * Second, 50 * Second], record.ChapterMarks!.Select(mark => mark.StartMicroseconds));
    Assert.Equal(100 * Second, record.ChapterMarksTotalMicroseconds);
    Assert.Equal(["a.mp3", "b.mp3"], record.ChapterMarksSources);
    var restored = QueueSnapshotMapper.Build(record, AppSettings.Defaults, null, out _)!;
    restored.SetAnalysis(Analysis());

    Assert.True(restored.HasChapterEdits);
    Assert.Equal(["Chapter 1", "Chapter 1 (2)", "Last"], restored.ChapterRows.Select(row => row.Title));
    Assert.False(restored.CanUndoChapters);
    Assert.Null(restored.ReviewReason);
    Assert.Equal(record.ChapterMarks, QueueSnapshotMapper.Capture(restored).ChapterMarks);
  }

  [Fact]
  public void AnUneditedBookSavesNoMarksAndAFileWithoutThemRestoresUnedited()
  {
    var record = QueueSnapshotMapper.Capture(Book());
    Assert.Null(record.ChapterMarks);
    var restored = QueueSnapshotMapper.Build(record, AppSettings.Defaults, null, out _)!;
    restored.SetAnalysis(Analysis());
    Assert.False(restored.HasChapterEdits);
  }

  [Theory]
  [InlineData("order")]
  [InlineData("tracks")]
  [InlineData("total")]
  public void RestoringOnDifferentTracksDropsTheMarksWithTheNote(string change)
  {
    var item = Book();
    item.Split(0, 20 * Second);
    var record = QueueSnapshotMapper.Capture(item);
    var restored = QueueSnapshotMapper.Build(record, AppSettings.Defaults, null, out _)!;

    restored.SetAnalysis(change switch
    {
      "order" => Analysis(100, "b.mp3", "a.mp3"),
      "tracks" => Analysis(100, "a.mp3", "b.mp3", "c.mp3"),
      _ => Analysis(120),
    });

    Assert.False(restored.HasChapterEdits);
    Assert.Null(restored.CreateOptions().ChapterMarks);
    Assert.Contains(BookQueueItemViewModel.ChapterEditsClearedNote, restored.Details, StringComparison.Ordinal);
    Assert.Contains(BookQueueItemViewModel.ChapterEditsClearedNote, restored.ReviewReason, StringComparison.Ordinal);
    Assert.Null(QueueSnapshotMapper.Capture(restored).ChapterMarks);
  }

  [Fact]
  public void MarksThatAreNotYetAppliedAreWrittenBackUnchanged()
  {
    var record = new QueueItemRecord(Root, SavedItemSettings.From(AppSettings.Defaults), ChapterMarks: [new SavedChapterMark(0, "One")],
      ChapterMarksTotalMicroseconds: 5, ChapterMarksSources: ["x"]);
    var restored = QueueSnapshotMapper.Build(record, AppSettings.Defaults, null, out _)!;

    var again = QueueSnapshotMapper.Capture(restored);

    Assert.Equal(record.ChapterMarks, again.ChapterMarks);
    Assert.Equal(5, again.ChapterMarksTotalMicroseconds);
    Assert.Equal(["x"], again.ChapterMarksSources);
  }
}
