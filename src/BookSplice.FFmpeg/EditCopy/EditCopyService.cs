using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookSplice.Core.Analysis;
using BookSplice.Core.Discovery;
using BookSplice.Core.EditCopy;
using BookSplice.Core.Execution;
using BookSplice.Core.Naming;
using BookSplice.Core.Validation;
using BookSplice.FFmpeg.Commands;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Tools;
using BookSplice.FFmpeg.Validation;

namespace BookSplice.FFmpeg.EditCopy;

// Writes an edited copy of an .m4b into the temporary folder, checks it against the original, and only then publishes it
// under a name that never replaces an existing file. The original is only ever opened for reading.
public sealed class EditCopyService : IEditCopyService
{
  private readonly IProcessRunner _runner;
  private readonly MediaToolSet _tools;
  private readonly FileConversionWorkspaceFactory _workspace;
  private readonly IEditCopyFileSystem _fileSystem;
  private readonly AtomicPublisher _publisher;

  public EditCopyService(IProcessRunner runner, MediaToolSet tools, string temporaryRoot, IEditCopyFileSystem? fileSystem = null, AtomicPublisher? publisher = null)
  {
    _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    _tools = tools ?? throw new ArgumentNullException(nameof(tools));
    _workspace = new FileConversionWorkspaceFactory(temporaryRoot);
    _fileSystem = fileSystem ?? new FileSystemEditCopyFileSystem();
    _publisher = publisher ?? new AtomicPublisher();
  }

  // Test seam: runs on the finished temporary copy just before it is validated.
  internal Func<string, CancellationToken, Task>? BeforeValidation { get; set; }

  public async Task<EditCopyResult> CreateAsync(EditCopyRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
  {
    ArgumentNullException.ThrowIfNull(request);
    var guard = EditCopyGuard.Check(request, _fileSystem);
    if (guard.Count > 0) return new(EditCopyStatus.InvalidInput, null, guard);
    var tags = EditCopyTagMapping.Map(request.Profile, request.TagEdits, out var mappingErrors);
    var invalid = mappingErrors.ToList();
    if (tags.TryGetValue("media_type", out var mediaType) && !Mp4MetadataWriter.IsValidMediaType(mediaType))
      invalid.Add(new(EditCopyCodes.TagValueInvalid, "The iTunes media type must be a number from 0 to 255."));
    if (invalid.Count > 0) return new(EditCopyStatus.InvalidInput, null, invalid);

    var source = Path.GetFullPath(request.SourcePath);
    var destination = Path.GetFullPath(request.DestinationPath);
    var artifacts = new List<string>();
    string? job = null;
    try
    {
      SourceIdentity identity;
      OriginalFacts original;
      using (ConversionPhaseScope.Start(progress, ConversionPhase.Preparation))
      {
        identity = SourceIdentity.Capture(source);
        var sourceReport = await Checker().CheckAsync(source, false, cancellationToken).ConfigureAwait(false);
        var sourceChecks = EditChecks(sourceReport);
        if (sourceReport.Facts is null || sourceChecks.Any(check => check.Required && !check.Passed))
          return new(EditCopyStatus.InvalidInput, null, [new(EditCopyCodes.SourceCheckFailed, "The file failed a required check, so it can't be edited.")], sourceChecks);
        original = await ReadOriginalAsync(source, sourceReport.Facts, cancellationToken).ConfigureAwait(false);
        var outOfRange = request.ChapterTitleEdits.Keys.Where(index => index >= original.Chapters.Count).Order().ToArray();
        if (outOfRange.Length > 0)
          return new(EditCopyStatus.InvalidInput, null, outOfRange.Select(index => new ExecutionDiagnostic(EditCopyCodes.ChapterIndexInvalid, $"Chapter {index + 1} does not exist.")));
        job = Path.GetFullPath(_workspace.CreateJobDirectory());
      }

      var copy = Register(artifacts, job, "edited.m4b");
      if (request.ChapterTitleEdits.Count == 0)
      {
        using (ConversionPhaseScope.Start(progress, ConversionPhase.MetadataWriting))
        {
          await CopyOriginalAsync(source, copy, cancellationToken).ConfigureAwait(false);
          Mp4MetadataWriter.Edit(copy, tags);
        }
      }
      else
      {
        using (ConversionPhaseScope.Start(progress, ConversionPhase.StreamCopy))
        {
          var metadata = Register(artifacts, job, "chapters.ffmeta");
          WriteChapterMetadata(metadata, original.Chapters, request.ChapterTitleEdits);
          var result = await _runner.RunAsync(new ProcessSpec(_tools.FFmpegPath, RemuxArguments(source, metadata, copy, original)), null, cancellationToken).ConfigureAwait(false);
          if (result.ExitCode != 0)
            return Fail(artifacts, job, EditCopyStatus.Failed, EditCopyCodes.ProcessFailed, $"FFmpeg exited with code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}.");
        }
        using (ConversionPhaseScope.Start(progress, ConversionPhase.MetadataWriting))
        {
          Mp4MetadataBoxes.RestoreUserData(copy, original.UserData);
          Mp4MetadataWriter.Edit(copy, tags);
        }
      }

      if (BeforeValidation is { } hook) await hook(copy, cancellationToken).ConfigureAwait(false);
      List<ValidationCheck> checks;
      using (var phase = ConversionPhaseScope.Start(progress, ConversionPhase.ValidationChecks))
        checks = await ValidateAsync(copy, original, request, tags, cancellationToken).ConfigureAwait(false);
      if (checks.Any(check => check.Required && !check.Passed))
        return Fail(artifacts, job, EditCopyStatus.ValidationFailed, EditCopyCodes.ValidationFailed, "The edited copy did not match the original, so nothing was saved.", checks);

      using (ConversionPhaseScope.Start(progress, ConversionPhase.Publication))
      {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SourceIdentity.Capture(source).Matches(identity))
          return Fail(artifacts, job, EditCopyStatus.Failed, EditCopyCodes.SourceChanged, "The original file changed while the copy was being made, so nothing was saved.", checks);
        if (EditCopyGuard.IsSameFile(source, destination, _fileSystem))
          return Fail(artifacts, job, EditCopyStatus.InvalidInput, EditCopyCodes.DestinationIsSource, "The destination is the file being edited. Choose a different name or folder.", checks);
        var published = await _publisher.PublishAsync(copy, destination, CollisionPolicy.AvoidCollision, cancellationToken).ConfigureAwait(false);
        if (published.Status != PublicationStatus.Published)
          return Fail(artifacts, job, EditCopyStatus.Failed, EditCopyCodes.PublicationFailed, "The edited copy could not be saved.", checks);
        Cleanup(artifacts, job, progress);
        return new(EditCopyStatus.Published, published.FinalPath, [], checks);
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      Cleanup(artifacts, job, progress);
      return new(EditCopyStatus.Cancelled, null, [new(EditCopyCodes.Cancelled, "The edit was cancelled. Nothing was saved.")]);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      Cleanup(artifacts, job, progress);
      return new(EditCopyStatus.Failed, null, [new(EditCopyCodes.Failed, "The edited copy could not be made. Nothing was saved.")]);
    }
  }

  private sealed record OriginalFacts(
    IReadOnlyList<RawChapter> Chapters,
    PacketDigest Packets,
    Mp4UserData UserData,
    IReadOnlyList<Mp4MetadataItem> Items,
    int? CoverStreamIndex,
    string? MajorBrand);

  internal sealed record RawChapter(long Start, long End, Rational TimeBase, string Title);

  internal readonly record struct PacketDigest(long Count, string Sha256);

  private async Task<OriginalFacts> ReadOriginalAsync(string source, MediaProbeResult probe, CancellationToken cancellationToken)
  {
    var chapters = await ReadChaptersAsync(source, cancellationToken).ConfigureAwait(false);
    var packets = await ReadPacketDigestAsync(source, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("The original audio packets could not be read.");
    return new OriginalFacts(chapters, packets, Mp4MetadataBoxes.ReadUserData(source), Mp4MetadataBoxes.ReadItems(source),
      probe.AttachedPictures.Count > 0 ? probe.AttachedPictures[0].Index : null, Mp4MetadataBoxes.ReadMajorBrand(source));
  }

  private static async Task CopyOriginalAsync(string source, string copy, CancellationToken cancellationToken)
  {
    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
    await using var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
  }

  private static string[] RemuxArguments(string source, string metadata, string copy, OriginalFacts original)
  {
    var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-i", source, "-f", "ffmetadata", "-i", metadata, "-map", "0:a:0" };
    if (original.CoverStreamIndex is { } cover) args.AddRange(["-map", $"0:{cover.ToString(CultureInfo.InvariantCulture)}", "-disposition:v:0", "attached_pic"]);
    args.AddRange(["-map_chapters", "1", "-c", "copy"]);
    if (original.MajorBrand is { } brand) args.AddRange(["-brand", brand]);
    args.AddRange(["-f", "mp4", "-y", copy]);
    return [.. args];
  }

  // Chapters keep the original's own time base and ticks, so their times carry over exactly.
  internal static void WriteChapterMetadata(string path, IReadOnlyList<RawChapter> chapters, IReadOnlyDictionary<int, string> titles)
  {
    var output = new StringBuilder(";FFMETADATA1\n");
    for (var index = 0; index < chapters.Count; index++)
    {
      var chapter = chapters[index];
      var title = titles.TryGetValue(index, out var edited) ? edited : chapter.Title;
      output.Append(CultureInfo.InvariantCulture, $"[CHAPTER]\nTIMEBASE={chapter.TimeBase.Numerator}/{chapter.TimeBase.Denominator}\nSTART={chapter.Start}\nEND={chapter.End}\ntitle={FFmetadataWriter.Escape(title)}\n");
    }
    File.WriteAllText(path, output.ToString(), new UTF8Encoding(false));
  }

  private async Task<List<ValidationCheck>> ValidateAsync(string copy, OriginalFacts original, EditCopyRequest request, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken)
  {
    var checks = new List<ValidationCheck>();
    checks.AddRange(EditChecks(await Checker().CheckAsync(copy, false, cancellationToken).ConfigureAwait(false)));

    var packets = await ReadPacketDigestAsync(copy, cancellationToken).ConfigureAwait(false);
    checks.Add(Check(EditCopyCodes.CheckAudioPackets, packets == original.Packets, "The audio packets differ from the original."));

    var items = Mp4MetadataBoxes.ReadItems(copy);
    checks.Add(Check(EditCopyCodes.CheckCover, CoverPayloads(items).SequenceEqual(CoverPayloads(original.Items), ByteArrayComparer.Instance), "The cover differs from the original."));
    checks.Add(Check(EditCopyCodes.CheckMetadataBoxes, SameUserData(Mp4MetadataBoxes.ReadUserData(copy), original.UserData), "Metadata stored outside the tag list differs from the original."));

    var chapters = await ReadChaptersAsync(copy, cancellationToken).ConfigureAwait(false);
    checks.Add(Check(EditCopyCodes.CheckChapterCount, chapters.Count == original.Chapters.Count, "The chapter count differs from the original."));
    var paired = original.Chapters.Zip(chapters).ToArray();
    checks.Add(Check(EditCopyCodes.CheckChapterTiming, chapters.Count == original.Chapters.Count && paired.All(pair => SameTime(pair.First.Start, pair.First.TimeBase, pair.Second.Start, pair.Second.TimeBase) && SameTime(pair.First.End, pair.First.TimeBase, pair.Second.End, pair.Second.TimeBase)), "A chapter start or end moved."));
    checks.Add(Check(EditCopyCodes.CheckChapterTitles, chapters.Count == original.Chapters.Count && paired.Select((pair, index) => string.Equals(pair.Second.Title, request.ChapterTitleEdits.TryGetValue(index, out var title) ? title : pair.First.Title, StringComparison.Ordinal)).All(value => value), "A chapter title does not read back as expected."));

    checks.Add(Check(EditCopyCodes.CheckEditedTags, tags.All(tag => ReadsBack(items, tag.Key, tag.Value)), "An edited tag does not read back as requested."));
    checks.Add(Check(EditCopyCodes.CheckUntouchedTags, Untouched(items, tags.Keys).SequenceEqual(Untouched(original.Items, tags.Keys), StringComparer.Ordinal), "A tag that was not edited changed."));
    return checks;
  }

  private ExistingM4bChecker Checker() => new(new FFprobeMediaProbe(_runner, _tools), _runner, _tools);

  // The existing-file checks that apply to an edit. audio.codec is not required here, so HE-AAC books can be edited:
  // the audio packets are copied, never re-encoded, and must hash the same as the original's. Every other required check still applies.
  internal static IReadOnlyList<ValidationCheck> EditChecks(ExistingFileCheckReport report)
    => report.Checks.Where(check => check.Status is ExistingCheckStatus.Passed or ExistingCheckStatus.Failed)
      .Select(check => new ValidationCheck(check.Code, check.Status == ExistingCheckStatus.Passed, check.Message, check.Required && check.Code != ValidationCodes.AudioCodec))
      .ToArray();

  // One tick of the coarser time base. Both sides are exact integer ticks (the copy is written from the original's ticks),
  // so any real difference is rounding by the muxer when it picks its own chapter time base.
  internal static bool SameTime(long left, Rational leftBase, long right, Rational rightBase)
  {
    var leftSeconds = (decimal)left * leftBase.Numerator / leftBase.Denominator;
    var rightSeconds = (decimal)right * rightBase.Numerator / rightBase.Denominator;
    var tolerance = decimal.Max((decimal)leftBase.Numerator / leftBase.Denominator, (decimal)rightBase.Numerator / rightBase.Denominator);
    return decimal.Abs(leftSeconds - rightSeconds) <= tolerance;
  }

  private static bool ReadsBack(IReadOnlyList<Mp4MetadataItem> items, string key, string value)
  {
    var atom = Mp4MetadataWriter.EditAtom(key);
    var freeform = items.Where(item => item.IsITunesFreeform(key)).ToArray();
    if (atom is null && value.Length > 0 && freeform.Length == 0) return false;
    if (!freeform.All(item => value.Length == 0 ? false : HasValue(item, value, isMediaType: false))) return false;
    if (atom is null) return true;
    var native = items.Where(item => item.Type == atom).ToArray();
    if (value.Length == 0) return native.Length == 0;
    return native.Length == 1 && HasValue(native[0], value, atom == Mp4MetadataWriter.MediaTypeAtom);
  }

  private static bool HasValue(Mp4MetadataItem item, string value, bool isMediaType)
  {
    if (item.Data.Count != 1) return false;
    var data = item.Data[0];
    if (isMediaType) return data.Payload.Length == 1 && data.Payload[0].ToString(CultureInfo.InvariantCulture) == value;
    return data.TypeCode == 1 && string.Equals(data.Text, value, StringComparison.Ordinal);
  }

  // TagLib renders every ilst item again when it saves (a data box's locale comes back as zero), so untouched items are
  // compared by type, freeform mean and name, and each data box's type code and payload instead of their raw bytes.
  private static IEnumerable<string> Untouched(IReadOnlyList<Mp4MetadataItem> items, IEnumerable<string> editedKeys)
  {
    var keys = editedKeys.ToArray();
    var atoms = keys.Select(Mp4MetadataWriter.EditAtom).OfType<string>().ToHashSet(StringComparer.Ordinal);
    return items.Where(item => !atoms.Contains(item.Type) && !keys.Any(item.IsITunesFreeform)).Select(Content);
  }

  internal static string Content(Mp4MetadataItem item)
    => string.Join(' ', [Hex(item.Type), Hex(item.Mean ?? ""), Hex(item.Name ?? ""), .. item.Data.Select(data => $"{data.TypeCode.ToString(CultureInfo.InvariantCulture)}:{Convert.ToHexString(data.Payload)}")]);

  private static string Hex(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));

  internal static bool SameUserData(Mp4UserData copy, Mp4UserData original)
    => copy.Preserved.SequenceEqual(original.Preserved, ByteArrayComparer.Instance);

  private static IEnumerable<byte[]> CoverPayloads(IReadOnlyList<Mp4MetadataItem> items)
    => items.Where(item => item.Type == "covr").SelectMany(item => item.Data).Select(data => data.Payload);

  // ffprobe reads chapter titles from the chapter text track, which keeps them whole. The Nero chpl box ffmpeg also writes
  // cuts titles at 255 bytes, the same as in a converted book, so it isn't what the titles are checked against.
  internal async Task<IReadOnlyList<RawChapter>> ReadChaptersAsync(string path, CancellationToken cancellationToken)
  {
    var result = await _runner.RunAsync(new ProcessSpec(_tools.FFprobePath, ["-v", "error", "-show_chapters", "-of", "json", path]), null, cancellationToken).ConfigureAwait(false);
    if (result.ExitCode != 0) throw new InvalidDataException("ffprobe could not read the chapters.");
    using var document = JsonDocument.Parse(result.StandardOutput);
    if (!document.RootElement.TryGetProperty("chapters", out var chapters)) return [];
    return chapters.EnumerateArray().Select(chapter =>
    {
      var parts = chapter.GetProperty("time_base").GetString()!.Split('/');
      var timeBase = new Rational(long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture));
      if (timeBase.Numerator <= 0 || timeBase.Denominator <= 0) throw new InvalidDataException("ffprobe returned an invalid chapter time base.");
      var title = chapter.TryGetProperty("tags", out var chapterTags) && chapterTags.TryGetProperty("title", out var value) ? value.GetString() ?? "" : "";
      return new RawChapter(chapter.GetProperty("start").GetInt64(), chapter.GetProperty("end").GetInt64(), timeBase, title);
    }).ToArray();
  }

  // Hashes every audio packet's SHA-256 line in order without keeping the lines, since a long book has about a million packets.
  internal async Task<PacketDigest?> ReadPacketDigestAsync(string path, CancellationToken cancellationToken)
  {
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    long count = 0;
    var malformed = false;
    void OnLine(string line)
    {
      if (line.Length == 0) return;
      if (!line.StartsWith("SHA256:", StringComparison.Ordinal)) malformed = true;
      hash.AppendData(Encoding.ASCII.GetBytes(line));
      hash.AppendData("\n"u8);
      count++;
    }
    var result = await _runner.RunAsync(new ProcessSpec(_tools.FFprobePath, ["-v", "error", "-select_streams", "a:0", "-show_data_hash", "sha256", "-show_entries", "packet=data_hash", "-of", "csv=p=0", path], StandardOutputLine: OnLine), null, cancellationToken).ConfigureAwait(false);
    if (result.ExitCode != 0 || malformed || count == 0) return null;
    return new PacketDigest(count, Convert.ToHexString(hash.GetHashAndReset()));
  }

  private static ValidationCheck Check(string code, bool passed, string failure) => new(code, passed, passed ? "Passed." : failure);

  private static string Register(List<string> artifacts, string job, string name)
  {
    var path = Path.GetFullPath(Path.Combine(job, name));
    artifacts.Add(path);
    return path;
  }

  private static EditCopyResult Fail(List<string> artifacts, string? job, EditCopyStatus status, string code, string message, IEnumerable<ValidationCheck>? checks = null)
  {
    FFmpegConversionExecutor.Cleanup(artifacts, job);
    return new(status, null, [new(code, message)], checks);
  }

  private static void Cleanup(List<string> artifacts, string? job, IProgress<ConversionProgress>? progress)
  {
    using (ConversionPhaseScope.Start(progress, ConversionPhase.Cleanup)) FFmpegConversionExecutor.Cleanup(artifacts, job);
  }

  private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
  {
    public static ByteArrayComparer Instance { get; } = new();
    public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);
    public int GetHashCode(byte[] obj) => obj.Length;
  }
}
