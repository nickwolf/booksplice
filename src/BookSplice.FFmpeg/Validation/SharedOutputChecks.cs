using System.Globalization;
using System.Text.RegularExpressions;
using BookSplice.Core.Analysis;
using BookSplice.Core.Execution;
using BookSplice.Core.Validation;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.FFmpeg.Validation;

internal static partial class SharedOutputChecks
{
  internal const long ChapterToleranceMicroseconds = 50_000;

  internal readonly record struct FullDecodeOutcome(ValidationCheck Check, string StandardError);

  internal static AudioTrack? PrimaryAudio(MediaProbeResult facts) => facts.AudioStreams.Count == 1 ? facts.AudioStreams[0] : null;

  internal static ValidationCheck AudioPrimaryCount(MediaProbeResult facts)
    => Check(ValidationCodes.AudioPrimaryCount, PrimaryAudio(facts) is not null, "The output must contain exactly one primary audio stream.");

  internal static ValidationCheck AudioCodec(MediaProbeResult facts)
  {
    var primary = PrimaryAudio(facts);
    return Check(ValidationCodes.AudioCodec, primary is not null && string.Equals(primary.CodecName, "aac", StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(primary.CodecProfile) || primary.CodecProfile.Contains("LC", StringComparison.OrdinalIgnoreCase)), "The primary audio stream is not AAC-LC.");
  }

  internal static ValidationCheck AudioExtra(MediaProbeResult facts)
    => Check(ValidationCodes.AudioExtra, facts.AudioStreams.Count <= 1, "The output contains extra audio streams.");

  internal static async Task<ValidationCheck> PacketEndRegionAsync(IProcessRunner runner, MediaToolSet tools, string path, CancellationToken cancellationToken)
  {
    try
    {
      var result = await runner.RunAsync(new ProcessSpec(tools.FFprobePath, ["-v", "error", "-select_streams", "a:0", "-read_intervals", "-2%+2", "-show_packets", "-of", "csv=p=0", path]), null, cancellationToken).ConfigureAwait(false);
      return Check(ValidationCodes.PacketsEndRegion, result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput), "The output audio packet end region could not be read.");
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception)
    {
      return Check(ValidationCodes.PacketsEndRegion, false, "The output audio packet end region could not be read.");
    }
  }

  internal static ValidationCheck ChaptersMonotonic(IReadOnlyList<MediaChapter> chapters)
  {
    var monotonic = chapters.All(chapter => chapter.EndTime > chapter.StartTime) && chapters.Zip(chapters.Skip(1), (left, right) => right.StartTime >= left.EndTime).All(value => value);
    return Check(ValidationCodes.ChaptersMonotonic, monotonic, "The output chapters are not positive and monotonic.");
  }

  // The chapter track can outlast the audio, and then the file duration follows the chapters, so the final end is also checked against the audio stream itself.
  internal static bool EndsPastAudio(decimal chapterEnd, IReadOnlyList<AudioTrack> audio)
  {
    if (audio is not [{ Duration: { } duration } primary]) return false;
    var audioEnd = (primary.StartTime ?? 0m) + duration;
    var frame = primary.SampleRate is > 0 ? 1024m / primary.SampleRate.Value : 0m;
    return chapterEnd - audioEnd > decimal.Max(ChapterToleranceMicroseconds / 1_000_000m, frame);
  }

  internal static async Task<FullDecodeOutcome> FullDecodeAsync(IProcessRunner runner, MediaToolSet tools, TimeProvider clock, string path, decimal? duration, IProgress<ConversionProgress>? progress, CancellationToken cancellationToken)
  {
    var total = ValidDurationMicroseconds(duration);
    using var phase = ConversionPhaseScope.Start(progress, ConversionPhase.FullDecode, total is not null);
    using var processProgress = new FullDecodeProgress(phase, total, clock);
    try
    {
      var result = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-v", "warning", "-progress", "pipe:1", "-nostats", "-i", path, "-map", "0:a:0", "-f", "null", "-"]), processProgress, cancellationToken).ConfigureAwait(false);
      var decodeError = result.ExitCode != 0 || Regex.IsMatch(result.StandardError, "\\b(error|invalid data)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
      return new(Check(ValidationCodes.FullDecode, !decodeError, "A full FFmpeg decode reported an error."), result.StandardError);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception)
    {
      return new(Check(ValidationCodes.FullDecode, false, "A full FFmpeg decode could not be started."), "");
    }
  }

  internal static ValidationCheck Check(string code, bool passed, string failure) => new(code, passed, passed ? "Passed." : failure);

  private static long? ValidDurationMicroseconds(decimal? duration)
  {
    if (duration is not > 0) return null;
    try
    {
      var microseconds = checked((long)decimal.Round(duration.Value * 1_000_000m, 0, MidpointRounding.AwayFromZero));
      return microseconds > 0 ? microseconds : null;
    }
    catch (OverflowException) { return null; }
  }

  private sealed class FullDecodeProgress(ConversionPhaseScope phase, long? duration, TimeProvider clock) : IProgress<string>, IDisposable
  {
    private readonly object _gate = new();
    private readonly long _started = clock.GetTimestamp();
    private long _processed;
    private bool _disposed;

    public void Report(string line)
    {
      var parsed = ConversionProgressParser.Parse(line, 0);
      if (parsed is null) return;
      lock (_gate)
      {
        if (_disposed) return;
        if (parsed.OutTimeMicroseconds is { } time && time >= 0)
          _processed = Math.Max(_processed, duration is { } total ? Math.Min(time, total) : time);
        double? percent = duration is { } valid ? 100d * _processed / valid : null;
        double? speed = null;
        var elapsed = clock.GetElapsedTime(_started, clock.GetTimestamp()).TotalSeconds;
        if (percent is not null && elapsed > 0 && _processed > 0)
        {
          var measured = _processed / 1_000_000d / elapsed;
          if (double.IsFinite(measured) && measured > 0) speed = measured;
        }
        phase.Report(new ConversionProgress(null, null, null, 0, PhasePercent: percent, PhaseSpeed: speed, ExpectsMeasurableUpdates: duration is not null));
      }
    }

    public void Dispose()
    {
      lock (_gate) _disposed = true;
    }
  }

  // ffmpeg lines every MP4 with a chapter track produces. They say nothing about the file being broken.
  internal static bool IsKnownNote(string line, MediaProbeResult facts)
  {
    var text = line.Trim();
    var timescale = TimescaleNotSet().Match(text);
    if (timescale.Success)
      return int.TryParse(timescale.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var audio) && facts.AudioStreams.Any(value => value.Index == audio);
    var match = UnsupportedChapterCodec().Match(text);
    if (!match.Success || !int.TryParse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return false;
    var stream = facts.OtherStreams?.SingleOrDefault(value => value.Index == index);
    return stream is not null && string.Equals(stream.CodecType, "data", StringComparison.OrdinalIgnoreCase) && string.Equals(stream.CodecTag, "text", StringComparison.OrdinalIgnoreCase);
  }

  [GeneratedRegex(@"^(\[[^\]]*\] )?stream (?<index>[0-9]+), timescale not set$", RegexOptions.CultureInvariant)]
  private static partial Regex TimescaleNotSet();

  // 98314 is ffmpeg's bin_data id, which the mov demuxer reports for the chapter text track.
  [GeneratedRegex(@"^Unsupported codec with id 98314 for input stream (?<index>[0-9]+)$", RegexOptions.CultureInvariant)]
  private static partial Regex UnsupportedChapterCodec();
}
