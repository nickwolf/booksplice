using System.Collections.ObjectModel;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.History;

public sealed class ConversionHistoryViewModel(ConversionHistoryReader reader) : ObservableModel, IDisposable
{
  private readonly CancellationTokenSource _lifetime = new();
  private IReadOnlyList<ConversionHistoryEntry> _all = [];
  private string _search = "";
  private string _status = "Loading history...";
  private bool _isLoading;
  private int _version;
  private bool _disposed;
  private ConversionHistoryEntry? _selected;
  public ObservableCollection<ConversionHistoryEntry> Entries { get; } = [];
  public ObservableCollection<SkippedHistoryRecord> Skipped { get; } = [];
  public bool HasSkipped => Skipped.Count > 0;
  public bool IsLoading { get => _isLoading; private set { _isLoading = value; Changed(); Changed(nameof(CanClear)); } }
  public bool CanClear => !IsLoading;
  public string Status { get => _status; private set { _status = value; Changed(); } }
  public string SearchText { get => _search; set { _search = value; Changed(); ApplyFilter(); } }
  public ConversionHistoryEntry? Selected
  {
    get => _selected;
    set { _selected = value; Changed(); Changed(nameof(CanOpenOutput)); Changed(nameof(HasSelection)); Changed(nameof(SelectedDetails)); }
  }
  public bool HasSelection => Selected is not null;
  public bool CanOpenOutput => Selected?.CanOpenOutput == true;
  public string SelectedDetails => Selected?.Details ?? "Select a record to see its details.";

  public async Task LoadAsync()
  {
    if (_disposed) return;
    var version = ++_version;
    IsLoading = true;
    Status = "Loading history...";
    try
    {
      var token = _lifetime.Token;
      var load = await Task.Run(() => reader.Read(token));
      if (version != _version) return;
      _all = load.Entries;
      Skipped.Clear();
      foreach (var record in load.Skipped) Skipped.Add(record);
      Changed(nameof(HasSkipped));
      ApplyFilter(load.NotLoaded);
    }
    catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { return; }
    finally { if (version == _version) IsLoading = false; }
  }

  public async Task DeleteSelectedAsync()
  {
    if (Selected is not { } entry) return;
    var deleted = await Task.Run(() => reader.Delete([entry]));
    await LoadAsync();
    Status = deleted == 1 ? "Deleted 1 record. Audio files were not touched." : $"The record could not be deleted. {Status}";
  }

  public async Task ClearAsync()
  {
    var deleted = await Task.Run(reader.DeleteAll);
    await LoadAsync();
    Status = $"Deleted {deleted} history records. Audio files were not touched.";
  }

  private int _notLoaded;

  private void ApplyFilter(int? notLoaded = null)
  {
    if (notLoaded is { } value) _notLoaded = value;
    var selected = Selected?.JobId;
    Entries.Clear();
    foreach (var entry in _all.Where(entry => entry.Matches(_search))) Entries.Add(entry);
    Selected = selected is { } id ? Entries.FirstOrDefault(entry => entry.JobId == id) : null;
    var count = Entries.Count;
    Status = (string.IsNullOrWhiteSpace(_search) ? $"{count} records" : $"{count} of {_all.Count} records match")
      + (Skipped.Count > 0 ? $", {Skipped.Count} could not be read" : "")
      + (_notLoaded > 0 ? $". {_notLoaded} older records were not loaded" : "");
  }

  // Null when the record can no longer be read.
  public async Task<string?> ReadDiagnosticsAsync()
  {
    if (Selected is not { } entry) return null;
    try { return await Task.Run(() => reader.ReadDiagnostics(entry)); }
    catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
  }

  public void Dispose() { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}
