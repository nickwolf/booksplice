using System.Globalization;
using System.Text.RegularExpressions;
using BookSplice.Core.Analysis;
using BookSplice.Core.Metadata;
using BookSplice.Core.Planning;
using BookSplice.Core.Validation;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tools;
using BookSplice.FFmpeg.Probing;

namespace BookSplice.FFmpeg.Validation;

public sealed class FFmpegOutputValidator : IOutputValidator
{
  private const long ChapterToleranceMicroseconds = 20_000;
  private readonly IMediaProbe _probe;
  private readonly IProcessRunner _runner;
  private readonly MediaToolSet _tools;
  private readonly ICoverPayloadValidator _coverPayloads;

  public FFmpegOutputValidator(IMediaProbe probe, IProcessRunner runner, MediaToolSet tools, ICoverPayloadValidator coverPayloads)
    => (_probe, _runner, _tools, _coverPayloads) = (probe, runner, tools, coverPayloads);

  public async Task<ValidationReport> ValidateAsync(ConversionPlan plan, string temporaryOutputPath, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(plan);
    ArgumentException.ThrowIfNullOrWhiteSpace(temporaryOutputPath);
    cancellationToken.ThrowIfCancellationRequested();
    var outputPath = Path.GetFullPath(temporaryOutputPath);
    var checks = new List<ValidationCheck>();
    var warnings = new List<string>();
    var errors = new List<string>();
    if (!File.Exists(outputPath)) return Failed(outputPath, checks, errors, ValidationCodes.FileExists, "The temporary output does not exist.", plan.PlanId);
    checks.Add(Pass(ValidationCodes.FileExists));

    MediaProbeResult facts;
    try { facts = await _probe.ProbeAsync(outputPath, cancellationToken).ConfigureAwait(false); }
    catch (OperationCanceledException) { throw; }
    catch (Exception exception) { return Failed(outputPath, checks, errors, ValidationCodes.ContainerParse, "The temporary output could not be parsed.", plan.PlanId, exception); }
    checks.Add(Pass(ValidationCodes.ContainerParse));
    warnings.AddRange(facts.Warnings.Select(Sanitize));

    ValidateAudio(plan, facts, checks);
    ValidateDuration(plan, facts, checks);
    await ValidatePacketEndAsync(outputPath, checks, cancellationToken).ConfigureAwait(false);
    ValidateChapters(plan, facts, checks);
    ValidateCover(plan, outputPath, facts, checks);
    ValidateMetadata(plan, facts, checks);
    if (plan.ValidationLevel == Core.Settings.ValidationLevel.Full) await ValidateFullDecodeAsync(outputPath, checks, warnings, cancellationToken).ConfigureAwait(false);
    var failed = checks.Where(check => check.Required && !check.Passed).ToArray();
    errors.AddRange(failed.Select(check => check.Message));
    return new ValidationReport(outputPath, failed.Length == 0, checks, facts, warnings, errors, plan.PlanId);
  }

  private static void ValidateAudio(ConversionPlan plan, MediaProbeResult facts, List<ValidationCheck> checks)
  {
    var audio = facts.AudioStreams;
    var primary = audio.Count == 1 ? audio[0] : null;
    checks.Add(Check(ValidationCodes.AudioPrimaryCount, primary is not null, "The output must contain exactly one primary audio stream."));
    checks.Add(Check(ValidationCodes.AudioCodec, primary is not null && string.Equals(primary.CodecName, "aac", StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(primary.CodecProfile) || primary.CodecProfile.Contains("LC", StringComparison.OrdinalIgnoreCase)), "The primary audio stream is not AAC-LC."));
    checks.Add(Check(ValidationCodes.AudioSampleRate, primary?.SampleRate == plan.SampleRate, "The primary audio stream does not have the planned sample rate."));
    checks.Add(Check(ValidationCodes.AudioLayout, primary?.Channels == plan.Channels && string.Equals(primary.ChannelLayout, plan.ChannelLayout, StringComparison.OrdinalIgnoreCase), "The primary audio stream does not have the planned channel layout."));
    checks.Add(Check(ValidationCodes.AudioExtra, audio.Count <= 1, "The output contains extra audio streams."));
  }

  private static void ValidateDuration(ConversionPlan plan, MediaProbeResult facts, List<ValidationCheck> checks)
  {
    if (plan.Chapters.Count == 0) { checks.Add(new ValidationCheck(ValidationCodes.DurationExpected, true, "No frozen duration evidence is available.", false)); return; }
    try
    {
      var expected = plan.Chapters[^1].EndMicroseconds;
      var actual = checked((long)decimal.Round((facts.Duration ?? -1m) * 1_000_000m, 0, MidpointRounding.AwayFromZero));
      var tolerance = DurationTolerance.Calculate(expected, plan.SourcePaths.Count);
      checks.Add(Check(ValidationCodes.DurationExpected, actual >= 0 && Math.Abs(checked(actual - expected)) <= tolerance, "The output duration is outside the planned tolerance."));
    }
    catch (OverflowException) { checks.Add(Check(ValidationCodes.DurationExpected, false, "The output duration cannot be represented safely.")); }
  }

  private async Task ValidatePacketEndAsync(string outputPath, List<ValidationCheck> checks, CancellationToken cancellationToken)
  {
    try
    {
      var result = await _runner.RunAsync(new ProcessSpec(_tools.FFprobePath, ["-v", "error", "-select_streams", "a:0", "-read_intervals", "-2%+2", "-show_packets", "-of", "csv=p=0", outputPath]), null, cancellationToken).ConfigureAwait(false);
      checks.Add(Check(ValidationCodes.PacketsEndRegion, result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput), "The output audio packet end region could not be read."));
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception)
    {
      checks.Add(Check(ValidationCodes.PacketsEndRegion, false, "The output audio packet end region could not be read."));
    }
  }

  private static void ValidateChapters(ConversionPlan plan, MediaProbeResult facts, List<ValidationCheck> checks)
  {
    if (plan.Chapters.Count == 0) return;
    var chapters = facts.Chapters;
    checks.Add(Check(ValidationCodes.ChaptersCount, chapters.Count == plan.Chapters.Count, "The output chapter count does not match the plan."));
    var monotonic = chapters.All(chapter => chapter.EndTime > chapter.StartTime) && chapters.Zip(chapters.Skip(1), (left, right) => right.StartTime >= left.EndTime).All(value => value);
    checks.Add(Check(ValidationCodes.ChaptersMonotonic, monotonic, "The output chapters are not positive and monotonic."));
    var timing = chapters.Count == plan.Chapters.Count && chapters.Zip(plan.Chapters, (actual, expected) => WithinChapterTolerance(actual.StartTime, expected.StartMicroseconds) && WithinChapterTolerance(actual.EndTime, expected.EndMicroseconds)).All(value => value);
    checks.Add(Check(ValidationCodes.ChaptersTiming, timing, "The output chapter timings do not match the plan."));
  }

  private void ValidateCover(ConversionPlan plan, string outputPath, MediaProbeResult facts, List<ValidationCheck> checks)
  {
    if (plan.Cover is null) { checks.Add(Check(ValidationCodes.CoverPresence, facts.AttachedPictures.Count == 0, "The output has an attached cover that was not planned.")); return; }
    var pictures = facts.AttachedPictures;
    var expectedCodec = plan.Cover.ContentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ? "png" : "mjpeg";
    var valid = pictures.Count == 1 && string.Equals(pictures[0].CodecName, expectedCodec, StringComparison.OrdinalIgnoreCase) && pictures[0].Width == plan.Cover.Width && pictures[0].Height == plan.Cover.Height;
    checks.Add(Check(ValidationCodes.CoverPresence, valid, "The planned attached cover is missing or has the wrong media type or dimensions."));
    if (!valid) { checks.Add(Check(ValidationCodes.CoverPayload, false, "The planned cover payload does not match.")); return; }
    try { checks.Add(Check(ValidationCodes.CoverPayload, _coverPayloads.GetPayloadHashes(outputPath).Any(hash => string.Equals(hash, plan.Cover.ContentHash, StringComparison.OrdinalIgnoreCase)), "The planned cover payload does not match.")); }
    catch (Exception) { checks.Add(Check(ValidationCodes.CoverPayload, false, "The planned cover payload could not be read.")); }
  }

  private static void ValidateMetadata(ConversionPlan plan, MediaProbeResult facts, List<ValidationCheck> checks)
  {
    var profile = plan.MetadataProfileId == "NickMp3tag" ? MetadataProfiles.NickMp3tag : MetadataProfiles.GenericMp4;
    var expected = profile.Apply(plan.Metadata).Where(pair => !string.IsNullOrWhiteSpace(pair.Value));
    var actual = new Dictionary<string, string>(facts.FormatTags, StringComparer.OrdinalIgnoreCase);
    if (profile.Name == MetadataProfiles.NickMp3tag.Name && actual.TryGetValue("album_artist", out var albumArtist)) actual["ALBUMARTIST"] = albumArtist;
    var valid = expected.All(pair => actual.TryGetValue(pair.Key, out var value) && string.Equals(value.TrimEnd('\0'), pair.Value.TrimEnd('\0'), StringComparison.Ordinal));
    checks.Add(Check(ValidationCodes.MetadataRequired, valid, "Required output metadata is missing or changed."));
  }

  private async Task ValidateFullDecodeAsync(string outputPath, List<ValidationCheck> checks, List<string> warnings, CancellationToken cancellationToken)
  {
    try
    {
      var result = await _runner.RunAsync(new ProcessSpec(_tools.FFmpegPath, ["-hide_banner", "-v", "warning", "-i", outputPath, "-map", "0:a:0", "-f", "null", "-"]), null, cancellationToken).ConfigureAwait(false);
      warnings.AddRange(result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(Sanitize));
      var decodeError = result.ExitCode != 0 || Regex.IsMatch(result.StandardError, "\\b(error|invalid data)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
      checks.Add(Check(ValidationCodes.FullDecode, !decodeError, "A full FFmpeg decode reported an error."));
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception)
    {
      checks.Add(Check(ValidationCodes.FullDecode, false, "A full FFmpeg decode could not be started."));
    }
  }

  private static bool WithinChapterTolerance(decimal seconds, long expectedMicroseconds)
  {
    var actual = checked((long)decimal.Round(seconds * 1_000_000m, 0, MidpointRounding.AwayFromZero));
    return Math.Abs(checked(actual - expectedMicroseconds)) <= ChapterToleranceMicroseconds;
  }
  private static ValidationCheck Pass(string code) => new(code, true, "Passed.");
  private static ValidationCheck Check(string code, bool passed, string failure) => new(code, passed, passed ? "Passed." : failure);
  private static ValidationReport Failed(string path, List<ValidationCheck> checks, List<string> errors, string code, string message, Guid planId, Exception? exception = null)
  {
    checks.Add(Check(code, false, message));
    errors.Add(message);
    if (exception is MediaProbeException probe) errors.AddRange(probe.Warnings.Select(Sanitize));
    return new ValidationReport(path, false, checks, warnings: [], errors: errors, planId: planId);
  }
  private static string Sanitize(string value) => Regex.Replace(value, @"[A-Za-z]:\\[^\s\r\n]*", "[path]");
}
