using System.Globalization;
using BookSplice.Core.Analysis;
using BookSplice.Core.Execution;
using BookSplice.Core.Validation;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.FFmpeg.Validation;

public sealed class ExistingM4bChecker : IExistingFileChecker
{
  public const string ChaptersWithinAudio = "chapters.within-audio";
  public const string ChaptersPresent = "chapters.present";
  public const string CoverFound = "cover.found";
  public const string TagsTitle = "tags.title";
  public const string TagsAuthor = "tags.author";
  public const string NeedsOriginals = "Needs the original tracks.";
  public const string FidelityNotEstablished = "Matches the original tracks: not established, the originals aren't available.";

  private static readonly string[] PlanOnlyCodes =
  [
    ValidationCodes.AudioSampleRate, ValidationCodes.AudioLayout, ValidationCodes.DurationExpected, ValidationCodes.ChaptersCount,
    ValidationCodes.ChaptersTiming, ValidationCodes.ChaptersTitles, ValidationCodes.CoverPresence, ValidationCodes.CoverPayload, ValidationCodes.MetadataRequired
  ];

  private readonly IMediaProbe _probe;
  private readonly IProcessRunner _runner;
  private readonly MediaToolSet _tools;
  private readonly TimeProvider _clock;

  public ExistingM4bChecker(IMediaProbe probe, IProcessRunner runner, MediaToolSet tools, TimeProvider? timeProvider = null)
    => (_probe, _runner, _tools, _clock) = (probe, runner, tools, timeProvider ?? TimeProvider.System);

  public async Task<ExistingFileCheckReport> CheckAsync(string path, bool fullDecode, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    cancellationToken.ThrowIfCancellationRequested();
    var fullPath = Path.GetFullPath(path);
    var checks = new List<ExistingFileCheck>();
    string[] notEstablished = [FidelityNotEstablished];
    using var checksPhase = ConversionPhaseScope.Start(progress, ConversionPhase.ValidationChecks);
    if (!File.Exists(fullPath))
    {
      checks.Add(Required(ValidationCodes.FileExists, false, "The file does not exist."));
      return new ExistingFileCheckReport(fullPath, checks, notEstablished);
    }
    checks.Add(Required(ValidationCodes.FileExists, true, "Passed."));
    ReportActivity(checksPhase);

    MediaProbeResult facts;
    try { facts = await _probe.ProbeAsync(fullPath, cancellationToken).ConfigureAwait(false); }
    catch (OperationCanceledException) { throw; }
    catch (Exception exception)
    {
      checks.Add(Required(ValidationCodes.ContainerParse, false, "The file could not be parsed."));
      return new ExistingFileCheckReport(fullPath, checks, notEstablished, warnings: exception is MediaProbeException probe ? probe.Warnings.Select(Sanitize) : []);
    }
    checks.Add(Required(ValidationCodes.ContainerParse, true, "Passed."));
    ReportActivity(checksPhase);

    checks.Add(Convert(SharedOutputChecks.AudioPrimaryCount(facts)));
    checks.Add(Convert(SharedOutputChecks.AudioCodec(facts)));
    checks.Add(Convert(SharedOutputChecks.AudioExtra(facts)));
    checks.Add(Convert(await SharedOutputChecks.PacketEndRegionAsync(_runner, _tools, fullPath, cancellationToken).ConfigureAwait(false)));
    ReportActivity(checksPhase);

    AddChapterChecks(facts, checks);
    checks.Add(CoverNote(facts));
    AddTagNotes(facts, checks);
    checks.AddRange(PlanOnlyCodes.Select(code => new ExistingFileCheck(code, ExistingCheckStatus.NotChecked, NeedsOriginals, false)));
    ReportActivity(checksPhase);
    checksPhase.Dispose();

    var warnings = facts.Warnings.Select(Sanitize).ToList();
    if (fullDecode)
    {
      var outcome = await SharedOutputChecks.FullDecodeAsync(_runner, _tools, _clock, fullPath, facts.Duration, progress, cancellationToken).ConfigureAwait(false);
      warnings.AddRange(outcome.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(Sanitize));
      checks.Add(Convert(outcome.Check));
    }
    else checks.Add(new ExistingFileCheck(ValidationCodes.FullDecode, ExistingCheckStatus.NotChecked, "Decode all audio was not selected.", false));
    warnings.RemoveAll(warning => SharedOutputChecks.IsKnownNote(warning, facts));
    return new ExistingFileCheckReport(fullPath, checks, notEstablished, facts, warnings);
  }

  private static void AddChapterChecks(MediaProbeResult facts, List<ExistingFileCheck> checks)
  {
    var chapters = facts.Chapters;
    if (chapters.Count == 0)
    {
      checks.Add(new ExistingFileCheck(ChaptersPresent, ExistingCheckStatus.Note, "No chapters.", false));
      return;
    }
    var order = Convert(SharedOutputChecks.ChaptersMonotonic(chapters));
    checks.Add(order.Status == ExistingCheckStatus.Failed ? order with { Message = "Chapters overlap, run out of order, or one has zero length." } : order);
    var past = SharedOutputChecks.EndsPastAudio(chapters[^1].EndTime, facts.AudioStreams);
    checks.Add(Required(ChaptersWithinAudio, !past, "The last chapter ends after the audio."));
  }

  private static ExistingFileCheck CoverNote(MediaProbeResult facts)
  {
    if (facts.AttachedPictures.Count == 0) return new ExistingFileCheck(CoverFound, ExistingCheckStatus.Note, "No cover.", false);
    var picture = facts.AttachedPictures[0];
    var size = picture.Width is { } width && picture.Height is { } height ? string.Create(CultureInfo.InvariantCulture, $" {width}x{height}") : "";
    return new ExistingFileCheck(CoverFound, ExistingCheckStatus.Note, $"Cover: {picture.CodecName ?? "unknown"}{size}", false);
  }

  private static void AddTagNotes(MediaProbeResult facts, List<ExistingFileCheck> checks)
  {
    if (!HasTag(facts, "title", "album")) checks.Add(new ExistingFileCheck(TagsTitle, ExistingCheckStatus.Note, "No title tag.", false));
    if (!HasTag(facts, "artist", "album_artist")) checks.Add(new ExistingFileCheck(TagsAuthor, ExistingCheckStatus.Note, "No author tag.", false));
  }

  private static bool HasTag(MediaProbeResult facts, params string[] keys)
    => keys.Any(key => facts.FormatTags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value.TrimEnd('\0')));

  private static ExistingFileCheck Required(string code, bool passed, string failure)
    => new(code, passed ? ExistingCheckStatus.Passed : ExistingCheckStatus.Failed, passed ? "Passed." : failure);

  private static ExistingFileCheck Convert(ValidationCheck check)
    => new(check.Code, check.Passed ? ExistingCheckStatus.Passed : ExistingCheckStatus.Failed, check.Message);

  private static string Sanitize(string value) => PublicTextRedactor.Sanitize(value);

  private static void ReportActivity(ConversionPhaseScope phase)
    => phase.Report(new ConversionProgress(null, null, null, 0));
}
