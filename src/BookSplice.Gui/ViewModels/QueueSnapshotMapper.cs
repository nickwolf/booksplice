using System.IO;
using BookSplice.Core.Discovery;
using BookSplice.Core.Metadata;
using BookSplice.Core.Queue;
using BookSplice.Core.Settings;
using BookSplice.Gui.Notifications;

namespace BookSplice.Gui.ViewModels;

internal static class QueueSnapshotMapper
{
  private const int MaximumMessageLength = 500;

  public static QueueSnapshot Capture(IEnumerable<BookQueueItemViewModel> items)
    => new(QueueSnapshot.CurrentSchemaVersion, DateTime.UtcNow, items.Select(Capture).ToArray());

  public static QueueItemRecord Capture(BookQueueItemViewModel item)
  {
    // Choices that could not be reapplied yet (the source was unavailable) are written back unchanged.
    var pending = item.ChoicesPending ? item.SavedRecord : null;
    var state = StateOf(item);
    return new QueueItemRecord(
      item.Source, SavedItemSettings.From(item.Settings),
      item.Selection is { } selection ? new SavedSelection(selection.Paths, selection.RootPath, selection.SharesRoot) : null,
      item.SavedIdentities ?? LiveIdentities(item),
      item.SelectedOrder, item.ReviewedOrder,
      pending is not null ? pending.Cover : LiveCover(item), item.OmitCover,
      pending is not null ? pending.ChapterTitles : item.Chapters.Where(chapter => chapter.Title != chapter.OriginalTitle).ToDictionary(chapter => chapter.Source, chapter => chapter.Title, StringComparer.Ordinal),
      pending is not null ? pending.Metadata : item.Metadata.Where(field => field.IsEdited).ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal),
      state, state == QueueItemState.Succeeded ? item.PublishedPath : null,
      state is QueueItemState.NotConverted or QueueItemState.Succeeded ? null : Truncate(MessageOf(item)));
  }

  private static QueueItemState StateOf(BookQueueItemViewModel item)
  {
    if (item.ConversionStarted || item.IsInterrupted) return QueueItemState.Interrupted;
    if (item.Status == "Complete" && item.Outcome is BookOutcome.Success or BookOutcome.Warning) return QueueItemState.Succeeded;
    return item.Outcome switch
    {
      BookOutcome.Failure => QueueItemState.Failed,
      BookOutcome.Cancelled => QueueItemState.Cancelled,
      _ => QueueItemState.NotConverted,
    };
  }

  private static string? MessageOf(BookQueueItemViewModel item)
    => item.Result is { } result ? (result.Summary.Length > 0 ? result.Summary : result.Title) : item.LastMessage;

  private static string? Truncate(string? text) => text is { Length: > MaximumMessageLength } ? text[..MaximumMessageLength] : text;

  private static IReadOnlyList<SourceIdentity>? LiveIdentities(BookQueueItemViewModel item)
  {
    var files = item.Analysis?.OrderedFiles;
    if (files is null or { Count: 0 }) return item.SavedRecord?.ExpectedSources;
    var identities = files.Select(file => file.Identity).OfType<SourceIdentity>().ToArray();
    return identities.Length == files.Count ? identities : null;
  }

  private static SavedCover? LiveCover(BookQueueItemViewModel item)
  {
    if (item.OmitCover || item.SelectedCover is not { } cover) return null;
    if (item.Analysis?.Cover.Selected?.ContentHash == cover.ContentHash) return null;
    return new SavedCover(cover.Origin, cover.SourcePath, cover.ContentHash, cover.EmbeddedPictureIndex);
  }

  /// <summary>Returns null, with a reason, for a saved book that cannot be rebuilt at all.</summary>
  public static BookQueueItemViewModel? Build(QueueItemRecord record, AppSettings baseSettings, MetadataProfileCatalog? profiles, out string? problem)
  {
    problem = null;
    SourceSelection? selection = null;
    try
    {
      if (record.Selection is { } saved) selection = saved.SharesRoot ? SourceSelection.Create(saved.Paths, saved.RootPath) : SourceSelection.Create(saved.Paths);
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
    {
      problem = $"{record.Source}: {exception.Message}";
      return null;
    }
    var item = new BookQueueItemViewModel(record.Source, record.Settings.ApplyTo(baseSettings), profiles)
    {
      Selection = selection,
      SelectedOrder = record.SelectedOrder,
      OmitCover = record.OmitCover,
      SavedRecord = record,
      ChoicesPending = true,
      IdentityCheckPending = true,
      SavedIdentities = record.ExpectedSources,
      LastMessage = record.Message,
    };
    item.ReviewedOrder = record.ReviewedOrder;
    if (record.ReviewedOrder is { } order) item.LoadTrackList(order.Included, order.Excluded);
    return item;
  }
}
