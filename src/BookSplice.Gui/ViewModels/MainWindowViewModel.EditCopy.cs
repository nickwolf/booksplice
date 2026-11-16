using System.Diagnostics;
using System.IO;
using BookSplice.Core.Analysis;
using BookSplice.Core.EditCopy;
using BookSplice.Core.Execution;
using BookSplice.Gui.Diagnostics;

namespace BookSplice.Gui.ViewModels;

// Saving an edited copy of an existing M4B. The original is never touched, and the book never enters the conversion queue's slots.
public sealed partial class MainWindowViewModel
{
  public bool CanEditCopies => editCopy is not null;

  private bool _preparingEditCopy;

  // The state for the Edit a copy window, or null with a message when the file's tags, chapters or profile are not available.
  // The chapters come from the edit service, which reads the same chapter track the edit will.
  public async Task<EditCopyViewModel?> PrepareEditCopyAsync(BookQueueItemViewModel item)
  {
    ArgumentNullException.ThrowIfNull(item);
    if (editCopy is null || !item.CanEditCopy || _preparingEditCopy) return null;
    if (item.EditProbe is not { } probe || item.InspectedFile is not { } source)
    {
      Message = "This file's tags could not be read, so it cannot be edited.";
      return null;
    }
    if (item.EditProfile is not { } profile)
    {
      Message = "The metadata profile for this book is unavailable. Choose another profile before editing a copy.";
      return null;
    }
    IReadOnlyList<EditCopyChapter> chapters;
    _preparingEditCopy = true;
    try { chapters = await editCopy.ReadChaptersAsync(source, CancellationToken.None); }
    catch (Exception)
    {
      Message = "This file's chapters could not be read, so it cannot be edited.";
      return null;
    }
    finally { _preparingEditCopy = false; }
    // The book may have been removed or started something else while its chapters were read.
    if (!item.CanEditCopy || !Items.Contains(item)) return null;
    return new EditCopyViewModel(source, probe, chapters, profile);
  }

  // The output folder from settings plus the original's file name. If that is the original itself (by path or, when it exists,
  // by file id, which catches a junction or another path to the same folder), the copy is named "<name> (edited).m4b".
  internal static string EditCopyDestination(string source, string outputDirectory)
  {
    var original = Path.GetFullPath(source);
    var candidate = Path.GetFullPath(Path.Combine(outputDirectory, Path.GetFileName(original)));
    return EditCopyGuard.IsSameFile(original, candidate, new FileSystemEditCopyFileSystem())
      ? Path.GetFullPath(Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(original) + " (edited).m4b"))
      : candidate;
  }

  public async Task EditCopyAsync(BookQueueItemViewModel item, EditCopyViewModel edits)
  {
    ArgumentNullException.ThrowIfNull(item);
    ArgumentNullException.ThrowIfNull(edits);
    if (editCopy is null || !item.CanEditCopy || !edits.CanCreate) return;
    using var operation = Enter("edit copy");
    if (operation is null) return;
    var request = edits.CreateRequest(EditCopyDestination(edits.SourcePath, item.OutputDirectory));
    item.IsBusy = true;
    item.Status = "Editing";
    item.Result = null;
    item.PublishedPath = null;
    item.Percent = 0;
    item.IsProgressIndeterminate = true;
    item.IsCancellationRequested = false;
    item.Details = "Starting edit";
    using var cancellation = new CancellationTokenSource();
    item.Cancellation = cancellation;
    using var tracked = ActiveJobRegistry.Shared.Track(cancellation);
    var started = DateTimeOffset.UtcNow;
    var elapsed = Stopwatch.StartNew();
    var display = new ConversionProgressDisplay();
    var reported = false;
    var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    timer.Tick += (_, _) => { if (elapsed.IsRunning && reported && ReferenceEquals(item.Cancellation, cancellation)) ApplySnapshot(); };
    var progress = new Progress<ConversionProgress>(value =>
    {
      if (dispatcher.CheckAccess()) ApplyProgress(value);
      else dispatcher.BeginInvoke(() => ApplyProgress(value));
    });
    EditCopyResult? result = null;
    ConversionTerminalStatus status;
    try
    {
      timer.Start();
      var run = Task.Run(() => editCopy.CreateAsync(request, cancellation.Token, progress));
      tracked.SetTask(run);
      result = await run;
      elapsed.Stop();
      if (result.Status == EditCopyStatus.Cancelled)
      {
        item.Status = "Edit cancelled";
        item.Details = "Edit cancelled";
        status = ConversionTerminalStatus.Cancelled;
      }
      else
      {
        item.Result = ConversionResultDisplay.CreateEdit(result, elapsed.Elapsed);
        item.Status = result.Status == EditCopyStatus.Published ? "Edited copy" : "Edit failed";
        item.Details = item.Result.Details;
        if (result.Status == EditCopyStatus.Published) item.PublishedPath = result.PublishedPath;
        status = result.Status switch
        {
          EditCopyStatus.Published => ConversionTerminalStatus.Succeeded,
          EditCopyStatus.ValidationFailed => ConversionTerminalStatus.ValidationFailed,
          EditCopyStatus.InvalidInput => ConversionTerminalStatus.InvalidInput,
          _ => ConversionTerminalStatus.ExecutionFailed,
        };
      }
    }
    catch (OperationCanceledException)
    {
      elapsed.Stop();
      item.Status = "Edit cancelled";
      item.Details = "Edit cancelled";
      status = ConversionTerminalStatus.Cancelled;
    }
    catch (Exception exception)
    {
      elapsed.Stop();
      item.Status = "Edit failed";
      item.Details = $"The edit could not run: {exception.Message}";
      status = ConversionTerminalStatus.UnexpectedFailure;
    }
    finally
    {
      timer.Stop();
      elapsed.Stop();
      item.IsProgressIndeterminate = false;
      item.Cancellation = null;
      item.IsBusy = false;
    }
    // An unexpected failure is shown on the book but not logged, so History only holds edits that reached a verdict or were cancelled.
    if (status == ConversionTerminalStatus.UnexpectedFailure || checkLog is null) return;
    try { await checkLog.WriteAsync(CreateEditRecord(request.SourcePath, status, result, started, DateTimeOffset.UtcNow), CancellationToken.None); }
    catch (Exception) { item.Details = string.Join(Environment.NewLine, new[] { item.Details, "The edit could not be recorded in History." }.Where(value => value.Length > 0)); }

    void ApplyProgress(ConversionProgress value)
    {
      if (!elapsed.IsRunning || !ReferenceEquals(item.Cancellation, cancellation) || item.Result is not null) return;
      if (!display.Report(value, elapsed.Elapsed)) return;
      reported = true;
      ApplySnapshot();
    }

    void ApplySnapshot()
    {
      var snapshot = display.Snapshot(elapsed.Elapsed);
      item.IsProgressIndeterminate = snapshot.Percent is null;
      item.Percent = snapshot.Percent ?? 0;
      item.Details = display.Describe(elapsed.Elapsed);
    }
  }

  // CheckedPath holds the original, so a failed or cancelled edit in History still says which book it was.
  internal static ConversionAuditRecord CreateEditRecord(string source, ConversionTerminalStatus status, EditCopyResult? result, DateTimeOffset started, DateTimeOffset finished)
  {
    var failed = status != ConversionTerminalStatus.Succeeded;
    var diagnostics = (result?.Checks ?? []).Select(check => new ServiceDiagnostic(check.Code,
        !check.Passed && check.Required ? ServiceDiagnosticSeverity.Error : ServiceDiagnosticSeverity.Information, check.Message))
      .Concat((result?.Diagnostics ?? []).Select(diagnostic => new ServiceDiagnostic(diagnostic.Code, failed ? ServiceDiagnosticSeverity.Error : ServiceDiagnosticSeverity.Information, diagnostic.Message)))
      .ToArray();
    return new(ConversionAuditRecord.CurrentSchemaVersion, Guid.NewGuid(), started, finished, status, null, null, null, null, null, [], null, null, null, null, [],
      diagnostics, result is { Status: EditCopyStatus.Published } ? result.PublishedPath : null, Kind: "edit", CheckedPath: Path.GetFullPath(source));
  }
}
