using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Queue;

namespace BookSplice.Gui.ViewModels;

// One line of the chapter grid, whether the book's chapters follow its tracks or have been edited.
public sealed record ChapterRow(int Index, long StartMicroseconds, string Start, string End, string Length, string Title, string Source, string? Problem);

// What the saved marks and the undo history depend on: the sources in order, and the total length.
internal sealed record TrackSignature(IReadOnlyList<string> Sources, long TotalMicroseconds)
{
  public bool Matches(IReadOnlyList<string>? sources, long? total) => total == TotalMicroseconds && sources is not null && sources.SequenceEqual(Sources, StringComparer.Ordinal);
}

public sealed partial class BookQueueItemViewModel
{
  public const string ChapterEditsClearedNote = "Chapter edits were cleared because the tracks changed.";
  private IReadOnlyList<ChapterMark>? _marks;
  private readonly Stack<IReadOnlyList<ChapterMark>?> _undo = new();

  /// <summary>True once a boundary edit has put the book on its own chapter marks. Until then chapters follow the tracks.</summary>
  public bool HasChapterEdits => _marks is not null;
  public bool CanUndoChapters => _undo.Count > 0;
  internal IReadOnlyList<ChapterMark>? ChapterMarksSnapshot => _marks;

  /// <summary>The first thing wrong with the edited chapters, or null. A book with a problem cannot convert.</summary>
  public string? ChapterProblem => _marks is null || Analysis?.Chapters is not { IsValid: true } plan ? null : DescribeFirst(ChapterMarks.Validate(_marks, plan.TotalDurationMicroseconds).Problems);

  public IReadOnlyList<ChapterRow> ChapterRows
  {
    get
    {
      if (Analysis?.Chapters is not { IsValid: true } plan) return [];
      var total = plan.TotalDurationMicroseconds;
      if (_marks is null)
        return plan.Entries.Select((entry, index) => Row(index, entry.StartMicroseconds, entry.EndMicroseconds, index < Chapters.Count ? Chapters[index].Title : entry.Title, entry.SourceRelativePath, null)).ToArray();
      var problems = ChapterMarks.Validate(_marks, total).Problems.Where(problem => problem.Index >= 0).GroupBy(problem => problem.Index)
        .ToDictionary(group => group.Key, group => string.Join(" ", group.Select(problem => problem.Message.TrimEnd('.') + ".")));
      return _marks.Select((mark, index) => Row(index, mark.StartMicroseconds, index + 1 < _marks.Count ? _marks[index + 1].StartMicroseconds : total, mark.Title,
        SourceAt(plan, mark.StartMicroseconds), problems.GetValueOrDefault(index))).ToArray();
    }
  }

  private static ChapterRow Row(int index, long start, long end, string title, string source, string? problem)
  {
    static long Clamp(long value) => Math.Max(0, value);
    return new ChapterRow(index, start, ChapterTime.Format(Clamp(start)), ChapterTime.Format(Clamp(end)), ChapterTime.Format(Clamp(end - start)), title, source, problem);
  }

  private static string SourceAt(ChapterPlan plan, long start) => (plan.Entries.LastOrDefault(entry => entry.StartMicroseconds <= start) ?? plan.Entries[0]).SourceRelativePath;

  private static string? DescribeFirst(IReadOnlyList<ChapterMarkProblem> problems)
    => problems.Count == 0 ? null : problems[0].Index >= 0 ? $"Chapter {problems[0].Index + 1}: {problems[0].Message.TrimEnd('.')}." : problems[0].Message;

  internal TrackSignature TrackSignature() => TrackSignatureOf(Analysis);

  private static TrackSignature TrackSignatureOf(BookAnalysis? analysis)
    => new(analysis?.OrderedFiles.Select(file => file.RelativePath).ToArray() ?? [], analysis?.Chapters is { IsValid: true } plan ? plan.TotalDurationMicroseconds : 0);

  public ChapterMarkEditResult Split(int index, long atMicroseconds) => Edit(index, (marks, total) => ChapterMarks.Split(marks, index, atMicroseconds, total));

  public ChapterMarkEditResult MergeWithNext(int index) => Edit(index, (marks, total) => ChapterMarks.MergeWithNext(marks, index, total));

  public ChapterMarkEditResult MoveStart(int index, long startMicroseconds) => Edit(index, (marks, total) => ChapterMarks.MoveStart(marks, index, startMicroseconds, total));

  /// <summary>Renames a chapter. An unedited book keeps its title edits on the track chapters; an edited one renames the mark.</summary>
  public ChapterMarkEditResult Rename(int index, string title)
  {
    if (_marks is not null) return Edit(index, (marks, total) => ChapterMarks.Rename(marks, index, title, total));
    if (Unavailable() is { } unavailable) return unavailable;
    if (index < 0 || index >= Chapters.Count) return Reject(index, ChapterMarkProblemCode.IndexOutOfRange, "There is no such chapter.");
    Chapters[index].Title = title;
    Changed(nameof(ChapterRows));
    return ChapterMarkEditResult.Accept(SeedMarks()!);
  }

  /// <summary>Goes back to one chapter per track. Undo brings the edited chapters back. False when there is nothing to reset.</summary>
  public bool ResetToTracks()
  {
    if (_marks is null || !CanEdit) return false;
    _undo.Push(_marks);
    _marks = null;
    RaiseChapterEditsChanged();
    return true;
  }

  /// <summary>Steps back one chapter edit, down to the unedited chapters. False when there is nothing to undo.</summary>
  public bool UndoChapters()
  {
    if (_undo.Count == 0 || !CanEdit) return false;
    _marks = _undo.Pop();
    RaiseChapterEditsChanged();
    return true;
  }

  private ChapterMarkEditResult Edit(int index, Func<IReadOnlyList<ChapterMark>, long, ChapterMarkEditResult> operation)
  {
    if (Unavailable() is { } unavailable) return unavailable;
    var current = _marks ?? SeedMarks();
    if (current is null) return Reject(index, ChapterMarkProblemCode.Unavailable, "The chapters of this book cannot be edited yet.");
    var result = operation(current, Analysis!.Chapters.TotalDurationMicroseconds);
    if (!result.IsAccepted) return result;
    _undo.Push(_marks);
    _marks = result.Marks;
    RaiseChapterEditsChanged();
    return result;
  }

  private ChapterMarkEditResult? Unavailable()
  {
    if (IsBusy) return Reject(-1, ChapterMarkProblemCode.Unavailable, "This book is converting. Wait for it to finish before editing its chapters.");
    if (!CanEdit) return Reject(-1, ChapterMarkProblemCode.Unavailable, "This book is already finished, so its chapters cannot change.");
    if (Analysis is not { Status: BookAnalysisStatus.Ready, Chapters.IsValid: true }) return Reject(-1, ChapterMarkProblemCode.Unavailable, "The chapters of this book cannot be edited until it has been analyzed.");
    return null;
  }

  private static ChapterMarkEditResult Reject(int index, ChapterMarkProblemCode code, string message) => ChapterMarkEditResult.Reject([new ChapterMarkProblem(index, code, message)]);

  // The track chapters with the title edits made so far, as the starting point for the first boundary edit.
  private IReadOnlyList<ChapterMark>? SeedMarks()
  {
    if (Analysis?.Chapters is not { IsValid: true } plan) return null;
    var entries = plan.Entries.Select((entry, index) => index < Chapters.Count ? entry with { Title = Chapters[index].Title } : entry);
    return ChapterMarks.FromChapterPlan(ChapterPlan.Valid(entries, plan.TotalDurationMicroseconds));
  }

  private void RaiseChapterEditsChanged()
  {
    foreach (var property in new[] { nameof(HasChapterEdits), nameof(CanUndoChapters), nameof(ChapterRows), nameof(ChapterProblem), nameof(CanConvert), nameof(QueueStatus) }) Changed(property);
    RaiseChoicesChanged();
  }

  // Marks only describe the tracks they were made on. A new analysis with other sources, another order or another length drops them and the history.
  private string? ClearMarksIfTracksChanged(BookAnalysis next)
  {
    if (_marks is null && _undo.Count == 0) return null;
    var now = TrackSignatureOf(next);
    if (TrackSignatureOf(Analysis).Matches(now.Sources, now.TotalMicroseconds)) return null;
    var hadMarks = _marks is not null;
    _marks = null;
    _undo.Clear();
    return hadMarks ? ChapterEditsClearedNote : null;
  }

  // Restored marks apply only to the tracks they were saved with.
  private IEnumerable<string> RestoreMarks(QueueItemRecord saved)
  {
    if (saved.ChapterMarks is not { } savedMarks) yield break;
    if (!TrackSignature().Matches(saved.ChapterMarksSources, saved.ChapterMarksTotalMicroseconds))
    {
      yield return ChapterEditsClearedNote;
      yield break;
    }
    _marks = savedMarks.Select(mark => new ChapterMark(mark.StartMicroseconds, mark.Title)).ToArray();
    _undo.Clear();
  }
}
