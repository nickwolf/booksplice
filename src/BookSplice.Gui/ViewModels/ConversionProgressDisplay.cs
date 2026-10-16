using System.Globalization;
using BookSplice.Core.Execution;

namespace BookSplice.Gui.ViewModels;

public sealed class ConversionProgressDisplay
{
  private readonly ConversionProgressObserver _observer = new();

  public string PhaseLabel => Label(_observer.Snapshot(TimeSpan.Zero).Phase);

  public bool Report(ConversionProgress progress, TimeSpan elapsed) => _observer.Report(progress, elapsed);

  public ConversionProgressSnapshot Snapshot(TimeSpan elapsed) => _observer.Snapshot(elapsed);

  public string Describe(TimeSpan elapsed)
  {
    var snapshot = Snapshot(elapsed);
    var parts = new List<string> { ElapsedText(elapsed), Label(snapshot.Phase) };
    if (snapshot.Percent is { } percent)
    {
      parts.Add(percent.ToString("0.#", CultureInfo.InvariantCulture) + "%");
      if (snapshot.Speed is { } speed)
        parts.Add("Speed: " + speed.ToString("0.0", CultureInfo.InvariantCulture) + "×");
      parts.Add("Phase remaining: " + (snapshot.Remaining is { } remaining ? FormatDuration(remaining) : "Unavailable"));
    }
    else
      parts.Add("Numeric progress unavailable");

    if (snapshot.CompletedSources is { } completed && snapshot.TotalSources is { } total)
      parts.Add(FormattableString.Invariant($"Sources: {completed}/{total}"));

    if (snapshot.Advisory == ProgressAdvisory.NoUpdate && snapshot.LastReportAge is { } reportAge)
      parts.Add($"No update received for {FormatDuration(reportAge)}; operation has not returned");
    else if (snapshot.Advisory == ProgressAdvisory.NoAdvance && snapshot.LastAdvanceAge is { } advanceAge)
      parts.Add($"Updates received, but progress has not advanced for {FormatDuration(advanceAge)}");

    return string.Join(" · ", parts);
  }

  public static string ElapsedText(TimeSpan elapsed) => $"Elapsed: {FormatDuration(elapsed)}";

  private static string Label(ConversionPhase? phase) => phase switch
  {
    ConversionPhase.Analysis => "Reanalyzing sources",
    ConversionPhase.Planning => "Planning conversion",
    ConversionPhase.Preparation => "Preparing conversion",
    ConversionPhase.Encoding => "Encoding audio",
    ConversionPhase.StreamCopy => "Copying audio",
    ConversionPhase.Assembly => "Assembling audio",
    ConversionPhase.MetadataWriting => "Writing metadata",
    ConversionPhase.ValidationChecks => "Checking output",
    ConversionPhase.FullDecode => "Validating full audio",
    ConversionPhase.Publication => "Publishing output",
    ConversionPhase.Cleanup => "Cleaning up",
    ConversionPhase.AuditWriting => "Writing audit",
    _ => "Starting conversion"
  };

  private static string FormatDuration(TimeSpan duration)
  {
    var seconds = (long)Math.Max(0, duration.TotalSeconds);
    return seconds >= 3600 ? FormattableString.Invariant($"{seconds / 3600}h {seconds / 60 % 60}m {seconds % 60}s")
      : seconds >= 60 ? FormattableString.Invariant($"{seconds / 60}m {seconds % 60}s")
      : FormattableString.Invariant($"{seconds}s");
  }
}
