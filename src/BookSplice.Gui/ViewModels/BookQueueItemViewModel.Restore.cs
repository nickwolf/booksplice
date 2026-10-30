using System.ComponentModel;
using System.Globalization;
using System.IO;
using BookSplice.Core.Analysis;
using BookSplice.Core.Discovery;
using BookSplice.Core.Metadata;
using BookSplice.Core.Planning;
using BookSplice.Core.Queue;
using BookSplice.Gui.Notifications;

namespace BookSplice.Gui.ViewModels;

// State carried over from a saved queue: what the book was, what the user chose, and what still needs a decision.
public sealed partial class BookQueueItemViewModel
{
  private string? _reviewReason;
  private string? _blockNote;

  /// <summary>The record this book was restored from. Its reviewed choices apply once a ready analysis can take them.</summary>
  internal QueueItemRecord? SavedRecord { get; set; }
  internal bool ChoicesPending { get; set; }
  internal bool IdentityCheckPending { get; set; }
  internal IReadOnlyList<SourceIdentity>? SavedIdentities { get; set; }
  internal string? LastMessage { get; set; }
  /// <summary>True from the moment a conversion holds a slot until it ends, so a snapshot taken then records it as interrupted.</summary>
  internal bool ConversionStarted { get; set; }
  public bool IsInterrupted { get; internal set; }

  public event EventHandler? ChoicesChanged;
  internal void RaiseChoicesChanged() => ChoicesChanged?.Invoke(this, EventArgs.Empty);

  /// <summary>Why the book cannot convert until the user looks at it again. Cleared by the next analysis the user asks for.</summary>
  public string? ReviewReason
  {
    get => _reviewReason;
    internal set { _reviewReason = value; Changed(); Changed(nameof(QueueStatus)); Changed(nameof(CanConvert)); }
  }

  public string? BlockReason
  {
    get
    {
      var lines = new List<string>();
      if (QualityProfileCatalog.FindById(Settings.QualityProfileId) is null)
        lines.Add($"The audio quality \"{Settings.QualityProfileId}\" is not available. Choose an audio quality for this book on the Output tab.");
      if (_profiles.Find(Settings.MetadataProfileId) is null)
        lines.Add($"The metadata profile \"{Settings.MetadataProfileId}\" is not available. Choose a metadata profile for this book on the Output tab.");
      return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }
  }

  private string? AttentionStatus => IsBusy || Status == "Complete" ? null : BlockReason is not null ? "Choose a profile" : ReviewReason is not null ? "Needs review" : null;

  private void ClearBlockNote()
  {
    if (_blockNote is null || BlockReason is not null) return;
    Details = Details.Replace(_blockNote, "", StringComparison.Ordinal).Trim();
    _blockNote = null;
  }

  private void AppendDetails(params string[] lines)
  {
    var extra = lines.Where(line => line.Length > 0).ToArray();
    if (extra.Length > 0) Details = string.Join(Environment.NewLine, new[] { Details }.Concat(extra).Where(line => line.Length > 0));
  }

  private void WatchReviewedChoices()
  {
    foreach (var field in Metadata) field.PropertyChanged += OnChoiceEdited;
    foreach (var chapter in Chapters) chapter.PropertyChanged += OnChoiceEdited;
    RaiseChoicesChanged();
  }

  private void OnChoiceEdited(object? sender, PropertyChangedEventArgs e) => RaiseChoicesChanged();

  // Runs inside SetAnalysis. The identity check happens once, on the first analysis that can be compared; later analyses are the user's decision.
  private List<string> ApplyRestoredState(BookAnalysis analysis)
  {
    if (SavedRecord is null) return [];
    var notes = new List<string>();
    if (IdentityCheckPending)
    {
      IdentityCheckPending = false;
      if (DescribeIdentityChange(analysis) is { } change) notes.Add(change);
    }
    if (ChoicesPending && analysis.Status == BookAnalysisStatus.Ready)
    {
      ChoicesPending = false;
      notes.AddRange(ApplyChoices(analysis, SavedRecord));
    }
    if (notes.Count > 0) ReviewReason = string.Join(" ", notes.Select(note => note.TrimEnd()));
    return notes;
  }

  private string? DescribeIdentityChange(BookAnalysis analysis)
  {
    if (SavedIdentities is not { } saved) return null;
    var current = analysis.OrderedFiles.Select(file => file.Identity).OfType<SourceIdentity>().ToArray();
    var byPath = current.ToDictionary(identity => identity.Path, StringComparer.OrdinalIgnoreCase);
    var savedPaths = saved.Select(identity => identity.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var changed = saved.Count(identity => byPath.TryGetValue(identity.Path, out var now) && !identity.Matches(now));
    var missing = saved.Count(identity => !byPath.ContainsKey(identity.Path));
    var added = current.Count(identity => !savedPaths.Contains(identity.Path));
    if (changed + missing + added == 0) return null;
    return string.Create(CultureInfo.InvariantCulture,
      $"The source files are not the ones reviewed before BookSplice closed ({changed} changed, {missing} missing, {added} new). Review the book again, then use Use selected order to confirm it.");
  }

  private IEnumerable<string> ApplyChoices(BookAnalysis analysis, QueueItemRecord saved)
  {
    foreach (var (name, value) in saved.Metadata ?? new Dictionary<string, string>())
    {
      if (Enum.TryParse<SemanticField>(name, out var semantic) && Metadata.FirstOrDefault(field => field.Field == semantic) is { } field) field.Value = value;
      else yield return $"The saved {name} edit no longer applies.";
    }
    foreach (var (source, title) in saved.ChapterTitles ?? new Dictionary<string, string>())
    {
      if (Chapters.FirstOrDefault(chapter => chapter.Source == source) is { } chapter) chapter.Title = title;
      else yield return $"The saved chapter title for {source} no longer applies.";
    }
    if (saved.Cover is { } cover)
    {
      var match = analysis.Cover.Candidates.FirstOrDefault(candidate => candidate.Origin == cover.Origin
        && string.Equals(candidate.SourcePath, cover.SourcePath, StringComparison.OrdinalIgnoreCase) && candidate.EmbeddedPictureIndex == cover.EmbeddedPictureIndex);
      if (match is null) yield return "The cover you chose is no longer available, so the default cover is selected.";
      else if (!string.Equals(match.ContentHash, cover.ContentHash, StringComparison.Ordinal)) yield return "The cover you chose has changed on disk, so the default cover is selected.";
      else SelectedCover = match;
    }
  }

  /// <summary>Restores a book that finished in an earlier session. It is not analyzed again, so a source that has since been moved does not matter.</summary>
  internal void RestoreCompleted()
  {
    var saved = SavedRecord!;
    Outcome = BookOutcome.Success;
    PublishedPath = saved.PublishedPath;
    Status = "Complete";
    Details = saved.PublishedPath is null ? "Completed in an earlier session."
      : File.Exists(saved.PublishedPath) ? $"Completed in an earlier session. Saved to: {saved.PublishedPath}"
      : $"Completed in an earlier session, but the file is no longer at: {saved.PublishedPath}";
  }

  /// <summary>Called after the analysis a restore starts. Applies the earlier outcome and explains anything that needs a decision.</summary>
  internal void CompleteRestore()
  {
    var saved = SavedRecord!;
    var ready = Analysis?.Status == BookAnalysisStatus.Ready;
    if (Analysis is null)
    {
      ReviewReason = "The source could not be analyzed. Your choices are kept. Use Use selected order to check the source again once it is available.";
      AppendDetails(ReviewReason);
    }
    var previous = saved.Message is { Length: > 0 } message ? $" ({message})" : "";
    switch (saved.State)
    {
      case QueueItemState.Failed:
        Outcome = BookOutcome.Failure;
        if (ready) Status = "Conversion failed";
        AppendDetails($"The last conversion attempt failed{previous}. Convert again to retry.");
        break;
      case QueueItemState.Cancelled:
        Outcome = BookOutcome.Cancelled;
        if (ready) Status = "Cancelled";
        AppendDetails("The last conversion attempt was cancelled.");
        break;
      case QueueItemState.Interrupted:
        IsInterrupted = true;
        if (ready) Status = "Interrupted";
        AppendDetails("BookSplice stopped while this book was converting, so the conversion did not finish and cannot be resumed. Convert it again to start over from the beginning.");
        break;
    }
    if (BlockReason is { } block)
    {
      _blockNote = block;
      AppendDetails(block);
    }
    Changed(nameof(QueueStatus));
    Changed(nameof(CanApplyTrackList));
  }
}
