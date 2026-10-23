namespace BookSplice.Gui.Diagnostics;

public sealed class CrashNoticeViewModel
{
  private readonly CrashReportStore _store;
  private readonly IReadOnlyList<CrashReportEntry> _entries;
  private readonly IReadOnlyList<string> _exactPaths;

  private CrashNoticeViewModel(CrashReportStore store, IReadOnlyList<CrashReportEntry> entries, IReadOnlyList<string> exactPaths)
  {
    _store = store;
    _entries = entries;
    _exactPaths = exactPaths;
  }

  public int ReportCount => _entries.Count;

  public string SummaryText => CrashSummaryBuilder.Build(_entries, _exactPaths);

  // Null when there is nothing to offer: candidate launches never show it, and a read failure just skips it.
  public static CrashNoticeViewModel? TryCreate(CrashLaunchMode mode, CrashReportStore store, IReadOnlyList<string> exactPaths)
  {
    if (mode != CrashLaunchMode.Normal) return null;
    try
    {
      var entries = store.ListUnreviewed();
      return entries.Count == 0 ? null : new CrashNoticeViewModel(store, entries, exactPaths);
    }
    catch { return null; }
  }

  public void MarkReviewed() => _store.MarkReviewed(_entries);
}
