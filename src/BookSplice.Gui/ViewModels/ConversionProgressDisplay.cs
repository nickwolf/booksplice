using System.Globalization;
using BookSplice.Core.Execution;

namespace BookSplice.Gui.ViewModels;

public sealed class ConversionProgressDisplay
{
  private readonly List<(TimeSpan Elapsed, double Percent)> _samples = [(TimeSpan.Zero, 0)];
  private double? _speed;
  private int? _stage;
  private double _percent;
  private bool _finalizing;

  public void Report(ConversionProgress progress, TimeSpan elapsed)
  {
    ArgumentNullException.ThrowIfNull(progress);
    if (_stage != progress.StageIndex)
    {
      _stage = progress.StageIndex;
      _speed = null;
    }
    if (progress.Speed is { } speed && double.IsFinite(speed) && speed >= 0) _speed = speed;
    if (progress.DisplayPercent is { } percent && double.IsFinite(percent) && percent > _percent)
    {
      _percent = Math.Clamp(percent, 0, 100);
      _samples.Add((elapsed, _percent));
      while (_samples.Count > 2 && _samples[1].Elapsed <= elapsed - TimeSpan.FromSeconds(30)) _samples.RemoveAt(0);
    }
    _finalizing |= progress.State == "completed";
  }

  public string Describe(TimeSpan elapsed)
  {
    var prefix = ElapsedText(elapsed);
    if (_finalizing) return $"{prefix} · Finalizing…";
    var speed = _speed is { } value ? value.ToString("0.0", CultureInfo.InvariantCulture) + "×" : "Calculating…";
    var remaining = EstimateRemaining(elapsed);
    return $"{prefix} · Speed: {speed} · Est. remaining: {(remaining is { } estimate ? FormatDuration(estimate) : "Calculating…")}";
  }

  public static string ElapsedText(TimeSpan elapsed) => $"Elapsed: {FormatDuration(elapsed)}";

  private TimeSpan? EstimateRemaining(TimeSpan elapsed)
  {
    if (elapsed.TotalSeconds < 3 || _samples.Count < 2 || _speed == 0) return null;
    var first = _samples[0];
    var last = _samples[^1];
    var age = (elapsed - last.Elapsed).TotalSeconds;
    var seconds = (last.Elapsed - first.Elapsed).TotalSeconds;
    var processed = last.Percent - first.Percent;
    if (age > 10 || seconds <= 0 || processed <= 0) return null;
    var remaining = (99 - last.Percent) * seconds / processed - age;
    return double.IsFinite(remaining) && remaining > 0 && remaining < TimeSpan.MaxValue.TotalSeconds
      ? TimeSpan.FromSeconds(remaining) : null;
  }

  private static string FormatDuration(TimeSpan duration)
  {
    var seconds = (long)Math.Max(0, duration.TotalSeconds);
    return seconds >= 3600 ? FormattableString.Invariant($"{seconds / 3600}h {seconds / 60 % 60}m {seconds % 60}s")
      : seconds >= 60 ? FormattableString.Invariant($"{seconds / 60}m {seconds % 60}s")
      : FormattableString.Invariant($"{seconds}s");
  }
}
