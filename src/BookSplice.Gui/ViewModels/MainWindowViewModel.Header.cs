using BookSplice.Core.Analysis;

namespace BookSplice.Gui.ViewModels;

// Plans the selected book in the background so its header always shows the file and audio strategy a conversion would use.
public sealed partial class MainWindowViewModel
{
  private BookQueueItemViewModel? _selectedItem;
  private int _headerVersion;
  private Task _headerTask = Task.CompletedTask;

  internal TimeSpan HeaderDebounce { get; set; } = TimeSpan.FromMilliseconds(300);
  internal TimeSpan HeaderGateRetry { get; set; } = TimeSpan.FromSeconds(1);

  public BookQueueItemViewModel? SelectedItem
  {
    get => _selectedItem;
    set
    {
      if (ReferenceEquals(_selectedItem, value)) return;
      _selectedItem = value;
      Changed();
      RequestHeader();
    }
  }

  internal Task HeaderTask => _headerTask;

  internal async Task HeaderSettledAsync()
  {
    Task current;
    do
    {
      current = _headerTask;
      await current;
    }
    while (!ReferenceEquals(current, _headerTask));
  }

  private void OnHeaderChoicesChanged(object? sender, EventArgs e) => RequestHeader();

  // Every change bumps the version, so a plan that was already running when the inputs changed is dropped when it returns.
  private void RequestHeader()
  {
    var version = ++_headerVersion;
    if (planner is null || _selectedItem is null) return;
    _headerTask = RefreshHeaderAsync(version);
  }

  private async Task RefreshHeaderAsync(int version)
  {
    if (HeaderDebounce > TimeSpan.Zero) await Task.Delay(HeaderDebounce);
    while (version == _headerVersion && _selectedItem is { } item)
    {
      if (!CanPlanHeader(item)) { item.Header = HeaderWithoutPlan(item); return; }
      // An update being prepared is not the user's problem: keep what the header shows and look again shortly.
      if (gate is { IsFrozen: true }) { await Task.Delay(HeaderGateRetry); continue; }
      using var operation = gate?.Enter("header");
      var header = await PlanHeaderAsync(item);
      if (version == _headerVersion && ReferenceEquals(item, _selectedItem)) item.Header = header;
      return;
    }
  }

  private static bool CanPlanHeader(BookQueueItemViewModel item) => !item.IsBusy && item.Status != "Complete" && item.Analysis?.Status == BookAnalysisStatus.Ready;

  // A book that is working keeps its header, a finished one drops it (the result card takes over), and anything else says why there is no plan.
  private static BookHeader? HeaderWithoutPlan(BookQueueItemViewModel item) => item switch
  {
    { Status: "Complete" } => null,
    { IsBusy: true } => item.Header,
    { Analysis: null } => BookHeader.Refused("The output is planned once the book has been analyzed."),
    { Analysis.Status: BookAnalysisStatus.NeedsDecision } => BookHeader.Refused("Choose a track order to plan the output."),
    _ => BookHeader.Refused("This book cannot be converted, so no output is planned."),
  };

  // Plans one book with the names its queued neighbours have claimed. Never touches the book's busy state, result or details.
  internal async Task<BookHeader> PlanHeaderAsync(BookQueueItemViewModel item)
  {
    if (item.BlockReason is { } block) return BookHeader.Refused(block);
    try
    {
      var analysis = item.Analysis!;
      var options = await OptionsWithQueuedNamesAsync(item);
      return BookHeader.From(await Task.Run(() => planner!.CreateAsync(analysis, options, CancellationToken.None)));
    }
    catch (Exception exception) { return BookHeader.Refused(exception.Message); }
  }
}
