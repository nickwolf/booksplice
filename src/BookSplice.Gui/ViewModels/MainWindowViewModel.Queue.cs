using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using BookSplice.Core.Queue;

namespace BookSplice.Gui.ViewModels;

// Saving and restoring the queue between sessions. Nothing here starts a conversion.
public sealed partial class MainWindowViewModel
{
  private static readonly HashSet<string> SaveTriggers = new(StringComparer.Ordinal)
  {
    nameof(BookQueueItemViewModel.Status), nameof(BookQueueItemViewModel.Settings), nameof(BookQueueItemViewModel.PublishedPath),
    nameof(BookQueueItemViewModel.Result), nameof(BookQueueItemViewModel.Analysis),
  };

  private QueuePersistence? _persistence;
  private string _queueNotice = "";
  public string QueueNotice { get => _queueNotice; private set { _queueNotice = value; Changed(); } }

  internal void FlushQueue() => _persistence?.Flush();

  // Call once, after the window exists. Reads the saved queue, rebuilds it, and from then on saves after changes.
  internal Task AttachQueueAsync(IQueueSnapshotStore store, TimeSpan? debounce = null, Action<Action>? post = null)
  {
    if (_persistence is not null) throw new InvalidOperationException("The queue is already attached.");
    _persistence = new QueuePersistence(store, () => QueueSnapshotMapper.Capture(Items), notice => QueueNotice = notice, debounce ?? TimeSpan.FromMilliseconds(750), post);
    Items.CollectionChanged += OnItemsChanged;
    return LoadAndRestoreAsync(store);
  }

  private async Task LoadAndRestoreAsync(IQueueSnapshotStore store)
  {
    using var hold = _persistence!.Hold();
    var loaded = await Task.Run(store.Load);
    switch (loaded.Code)
    {
      case QueueLoadCode.Corrupt:
        QueueNotice = $"The saved queue could not be read, so BookSplice started with an empty queue. The unreadable file was kept as {Path.GetFileName(loaded.PreservedPath)} in the BookSplice data folder.";
        return;
      case QueueLoadCode.FutureVersion:
        QueueNotice = "The saved queue was written by a newer version of BookSplice. It was left untouched and BookSplice started with an empty queue. Changes to the queue will not be saved in this session.";
        return;
      case QueueLoadCode.Unreadable:
        QueueNotice = $"The saved queue could not be opened ({loaded.Detail}), so BookSplice started with an empty queue. Changes to the queue will not be saved in this session.";
        return;
    }
    if (loaded.Snapshot is { Items.Count: > 0 } snapshot) await RestoreAsync(snapshot);
  }

  private async Task RestoreAsync(QueueSnapshot snapshot)
  {
    var problems = new List<string>();
    var restored = new List<BookQueueItemViewModel>();
    foreach (var record in snapshot.Items)
    {
      var item = QueueSnapshotMapper.Build(record, Settings, profiles, out var problem);
      if (item is null) { problems.Add($"A saved book could not be restored. {problem}"); continue; }
      restored.Add(item);
    }
    var toAnalyze = new List<BookQueueItemViewModel>();
    foreach (var item in restored)
    {
      if (item.SavedRecord!.State == QueueItemState.Succeeded) item.RestoreCompleted();
      else { item.MarkWaiting(); toAnalyze.Add(item); }
      Items.Add(item);
    }
    var interrupted = restored.Count(item => item.SavedRecord!.State == QueueItemState.Interrupted);
    QueueNotice = string.Create(CultureInfo.InvariantCulture, $"Restored {restored.Count} book(s) from your last session. Nothing was started automatically.")
      + (interrupted > 0 ? string.Create(CultureInfo.InvariantCulture, $" {interrupted} book(s) were interrupted and will not resume. Convert them again when you are ready.") : "");
    if (problems.Count > 0) Message = string.Join(Environment.NewLine, problems);
    using var operation = Enter("restore");
    var resolved = 0;
    try
    {
      if (operation is null) return;
      foreach (var item in toAnalyze)
      {
        if (!item.IsWaiting || !Items.Contains(item)) { resolved++; continue; }
        BatchProgress = $"Analyzing {resolved + 1} of {toAnalyze.Count}";
        await AnalyzeAsync(item);
        item.CompleteRestore();
        resolved++;
      }
    }
    finally
    {
      foreach (var item in toAnalyze) if (item.IsWaiting) item.CancelWaiting();
      BatchProgress = "";
    }
  }

  private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
  {
    if (e.OldItems is not null) foreach (BookQueueItemViewModel item in e.OldItems) { item.PropertyChanged -= OnItemChanged; item.ChoicesChanged -= OnChoicesChanged; }
    if (e.NewItems is not null) foreach (BookQueueItemViewModel item in e.NewItems) { item.PropertyChanged += OnItemChanged; item.ChoicesChanged += OnChoicesChanged; }
    _persistence?.Request();
  }

  private void OnChoicesChanged(object? sender, EventArgs e) => _persistence?.Request();

  private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
  {
    if (e.PropertyName is null || !SaveTriggers.Contains(e.PropertyName)) return;
    // A book that has just taken a conversion slot is recorded at once, so a forced exit still finds it marked.
    _persistence?.Request(immediate: sender is BookQueueItemViewModel { ConversionStarted: true, Status: "Converting" });
  }
}
