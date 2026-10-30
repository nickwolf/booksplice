using System.Collections.ObjectModel;
using System.IO;
using BookSplice.Core.Discovery;

namespace BookSplice.Gui.ViewModels;

public sealed class BookGroupRow(BookGroup group, string parentPath)
{
  public BookGroup Group { get; } = group;
  public string Title => Group.Name;
  public string Summary => $"{Group.Files.Count} {(Group.Files.Count == 1 ? "file" : "files")}{(Group.Parts.Count > 1 ? $" in {Group.Parts.Count} folders" : "")}";
  public IReadOnlyList<string> Lines { get; } = group.Files.Select(file => Path.GetRelativePath(parentPath, file)).ToArray();
}

public delegate Task<BookGroupPlan> BookGroupPlanFactory(string parentPath, IProgress<BookGroupScanProgress> progress, CancellationToken cancellationToken);

public sealed class BookGroupPreviewViewModel(string parentPath, BookGroupPlanFactory? plan = null) : ObservableModel
{
  private readonly BookGroupPlanFactory _plan = plan ?? ((path, progress, token) => new BookGroupPlanner().PlanAsync(path, progress, token));
  private string _status = "Scanning...";
  private bool _scanning;
  private Action? _cancelScan;

  public string ParentPath { get; } = Path.GetFullPath(parentPath);
  public ObservableCollection<BookGroupRow> Groups { get; } = [];
  public ObservableCollection<string> NotInspected { get; } = [];
  public string Status { get => _status; private set { _status = value; Changed(); } }
  public bool IsScanning { get => _scanning; private set { _scanning = value; Changed(); Changed(nameof(CanAdd)); } }
  public bool CanAdd => !IsScanning && Groups.Count > 0;
  public string AddLabel => $"_Add {Groups.Count} {(Groups.Count == 1 ? "book" : "books")}";
  public IReadOnlyList<BookGroup> AcceptedGroups => Groups.Select(row => row.Group).ToArray();

  public void CancelScan() => _cancelScan?.Invoke();

  public async Task LoadAsync(CancellationToken cancellationToken = default)
  {
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    _cancelScan = cancellation.Cancel;
    Groups.Clear();
    NotInspected.Clear();
    IsScanning = true;
    Status = "Scanning...";
    try
    {
      var progress = new Progress<BookGroupScanProgress>(value => { if (IsScanning) Status = $"Scanned {value.Folders} folders, found {value.Files} audio files"; });
      var result = await _plan(ParentPath, progress, cancellation.Token);
      foreach (var group in result.Groups) Groups.Add(new BookGroupRow(group, ParentPath));
      foreach (var problem in result.NotInspected)
        NotInspected.Add(problem.Code == "discovery.reparse-point-skipped" ? $"Skipped (reparse point): {problem.FullPath}" : $"Could not be inspected: {problem.FullPath} ({problem.Message})");
      Status = Groups.Count == 0 ? "No audio files were found." : $"Found {Groups.Count} proposed {(Groups.Count == 1 ? "book" : "books")}.";
    }
    catch (OperationCanceledException)
    {
      Status = "Scan cancelled.";
      throw;
    }
    finally
    {
      _cancelScan = null;
      IsScanning = false;
      Refresh();
    }
  }

  /// <summary>Joins two or more groups at the position of the first one.</summary>
  public bool Combine(IReadOnlyList<BookGroupRow> selected)
  {
    var rows = InOrder(selected);
    if (rows.Length < 2) return false;
    var index = Groups.IndexOf(rows[0]);
    foreach (var row in rows) Groups.Remove(row);
    Groups.Insert(index, new BookGroupRow(BookGroup.Combine(rows.Select(row => row.Group).ToArray()), ParentPath));
    Refresh();
    return true;
  }

  /// <summary>Replaces each selected multi-folder group with one group per folder.</summary>
  public bool Split(IReadOnlyList<BookGroupRow> selected)
  {
    var rows = InOrder(selected).Where(row => row.Group.CanSplit).ToArray();
    foreach (var row in rows)
    {
      var index = Groups.IndexOf(row);
      Groups.RemoveAt(index);
      foreach (var part in row.Group.Split()) Groups.Insert(index++, new BookGroupRow(part, ParentPath));
    }
    Refresh();
    return rows.Length > 0;
  }

  public bool Remove(IReadOnlyList<BookGroupRow> selected)
  {
    var rows = InOrder(selected);
    foreach (var row in rows) Groups.Remove(row);
    Refresh();
    return rows.Length > 0;
  }

  private BookGroupRow[] InOrder(IReadOnlyList<BookGroupRow> selected) => Groups.Where(selected.Contains).ToArray();

  private void Refresh()
  {
    Changed(nameof(AddLabel));
    Changed(nameof(CanAdd));
  }
}
