using System.Diagnostics;
using BookSplice.Core.Analysis;
using BookSplice.Core.Execution;
using BookSplice.Core.Settings;
using BookSplice.Core.Validation;
using BookSplice.Gui.Diagnostics;

namespace BookSplice.Gui.ViewModels;

// Checking an existing M4B. The file is only read, and the book never enters the conversion queue's slots.
public sealed partial class MainWindowViewModel
{
  public async Task CheckAsync(BookQueueItemViewModel item)
  {
    if (checker is null || !item.CanCheck) return;
    using var operation = Enter("check");
    if (operation is null) return;
    var path = item.Source;
    var fullDecode = item.DecodeAllAudio;
    item.IsBusy = true;
    item.Status = "Checking";
    item.Result = null;
    item.PublishedPath = null;
    item.Percent = 0;
    item.IsProgressIndeterminate = true;
    item.IsCancellationRequested = false;
    item.Details = "Starting check";
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
    ExistingFileCheckReport? report = null;
    ConversionTerminalStatus status;
    try
    {
      timer.Start();
      var run = Task.Run(() => checker.CheckAsync(path, fullDecode, cancellation.Token, progress));
      tracked.SetTask(run);
      report = await run;
      elapsed.Stop();
      item.Result = ConversionResultDisplay.CreateCheck(report, elapsed.Elapsed);
      item.Status = report.IsValid ? "Checked" : "Check failed";
      item.Details = item.Result.Details;
      status = report.IsValid ? ConversionTerminalStatus.Succeeded : ConversionTerminalStatus.ValidationFailed;
    }
    catch (OperationCanceledException)
    {
      elapsed.Stop();
      item.Status = "Check cancelled";
      item.Details = "Check cancelled";
      status = ConversionTerminalStatus.Cancelled;
    }
    catch (Exception exception)
    {
      elapsed.Stop();
      item.Status = "Check failed";
      item.Details = $"The check could not run: {exception.Message}";
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
    // An unexpected failure is shown on the book but not logged, so History only holds checks that reached a verdict or were cancelled.
    if (status == ConversionTerminalStatus.UnexpectedFailure || checkLog is null) return;
    try { await checkLog.WriteAsync(CreateCheckRecord(path, fullDecode, status, report, started, DateTimeOffset.UtcNow), CancellationToken.None); }
    catch (Exception) { item.Details = string.Join(Environment.NewLine, new[] { item.Details, "The check could not be recorded in History." }.Where(value => value.Length > 0)); }

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

  internal static ConversionAuditRecord CreateCheckRecord(string path, bool fullDecode, ConversionTerminalStatus status, ExistingFileCheckReport? report, DateTimeOffset started, DateTimeOffset finished)
    => new(ConversionAuditRecord.CurrentSchemaVersion, Guid.NewGuid(), started, finished, status, null, null, null, null, null, [], null, null, null, null, [],
      (report?.Checks ?? []).Select(check => new ServiceDiagnostic("check." + check.Code,
        check.Status == ExistingCheckStatus.Failed && check.Required ? ServiceDiagnosticSeverity.Error : ServiceDiagnosticSeverity.Information, check.Message)).ToArray(),
      null, Kind: "check", CheckedPath: System.IO.Path.GetFullPath(path), CheckLevel: fullDecode ? "Full decode" : "Lightweight");
}
