using System.Globalization;
using System.Text.Json;
using BookSplice.Core.Analysis;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.FFmpeg.Probing;

public sealed class FFprobeMediaProbe : IMediaProbe
{
  private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
  private readonly IProcessRunner _runner;
  private readonly MediaToolSet _tools;
  public FFprobeMediaProbe(IProcessRunner runner, MediaToolSet tools) { _runner = runner; _tools = tools; }

  public async Task<MediaProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken = default)
  {
    var spec = new ProcessSpec(_tools.FFprobePath, ["-v", "warning", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", "-show_data_hash", "sha256", "-show_error", inputPath]);
    var result = await _runner.RunAsync(spec, null, cancellationToken).ConfigureAwait(false);
    var warnings = result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToArray();
    if (result.ExitCode != 0) throw new MediaProbeException($"ffprobe failed with exit code {result.ExitCode}.", warnings);
    FFprobeDocument document;
    try { document = JsonSerializer.Deserialize<FFprobeDocument>(result.StandardOutput, JsonOptions) ?? throw new JsonException("empty document"); }
    catch (JsonException exception) { throw new MediaProbeException("ffprobe returned malformed JSON.", warnings, exception); }
    var formatTags = ToTags(document.Format?.Tags);
    var audio = (document.Streams ?? []).Where(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)).Select(ToAudio).ToArray();
    var covers = (document.Streams ?? []).Where(s => s.Disposition?.AttachedPic == 1).Select(s => new MediaAttachedPicture(s.Index, s.CodecName, s.Width, s.Height, ToRawTags(s.Tags))).ToArray();
    var duration = Decimal(document.Format?.Duration);
    IReadOnlyList<MediaChapter> chapters = (document.Chapters ?? []).Select(c => new MediaChapter(c.Id, RequiredTimestamp(c.StartTime, "start_time"), RequiredTimestamp(c.EndTime, "end_time"), RequiredRational(c.TimeBase), ToRawTags(c.Tags))).ToArray();
    var extension = Path.GetExtension(inputPath);
    if (extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) || extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
      chapters = NeroChapterReader.Read(inputPath, duration, chapters) ?? chapters;
    if (audio.Length == 1 && (string.Equals(audio[0].CodecName, "mp3", StringComparison.OrdinalIgnoreCase) || string.Equals(audio[0].CodecName, "aac", StringComparison.OrdinalIgnoreCase)))
    {
      duration = await GetExactDurationAsync(inputPath, audio[0], cancellationToken).ConfigureAwait(false);
      audio[0] = audio[0] with { Duration = duration };
    }
    var otherStreams = (document.Streams ?? []).Where(s => !string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase) && s.Disposition?.AttachedPic != 1)
      .Select(s => new MediaStreamInfo(s.Index, s.CodecType, s.CodecName, s.CodecTag)).ToArray();
    return new MediaProbeResult(audio, covers, chapters, formatTags, ToRawTags(document.Format?.Tags), warnings, duration, document.Format?.FormatName, Long(document.Format?.Size), otherStreams);
  }

  // Every packet is read in order because the demuxer attaches the end padding only to the real last packet.
  private async Task<decimal> GetExactDurationAsync(string inputPath, AudioTrack track, CancellationToken cancellationToken)
  {
    var result = await _runner.RunAsync(new ProcessSpec(_tools.FFprobePath,
      ["-v", "error", "-select_streams", "a:0", "-show_packets", "-show_entries", "packet=duration:packet_side_data=side_data_type,skip_samples,discard_padding", "-of", "csv=p=0", inputPath]), null, cancellationToken).ConfigureAwait(false);
    if (result.ExitCode != 0) throw new MediaProbeException("ffprobe could not read exact duration evidence.", result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    return PacketDuration(result.StandardOutput, track);
  }

  private static decimal PacketDuration(string packets, AudioTrack track)
  {
    long packetCount = 0;
    string? first = null;
    // A short first or last packet must not set the packet size, so the most common duration wins.
    var durations = new Dictionary<long, long>();
    foreach (var line in packets.AsSpan().EnumerateLines())
    {
      if (line.IsWhiteSpace()) continue;
      packetCount++;
      first ??= line.Trim().ToString();
      var field = line.Trim();
      var comma = field.IndexOf(',');
      if (comma >= 0) field = field[..comma];
      if (long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration) && duration > 0)
        durations[duration] = durations.GetValueOrDefault(duration) + 1;
    }
    if (first is null) throw new MediaProbeException("ffprobe did not return packet evidence.", []);
    var trimmed = packets.AsSpan().TrimEnd();
    var last = trimmed[(trimmed.LastIndexOfAny('\r', '\n') + 1)..].Trim().ToString();
    if (track.SampleRate is not > 0 || track.TimeBase is not { } timeBase)
      throw new MediaProbeException("ffprobe returned incomplete packet-count evidence.", []);

    if (durations.Count == 0) throw new MediaProbeException("ffprobe omitted the packet duration.", []);
    var packetDuration = durations.MaxBy(pair => pair.Value).Key;
    var samplesPerPacketValue = checked((decimal)packetDuration * timeBase.Numerator * track.SampleRate.Value / timeBase.Denominator);
    var samplesPerPacket = checked((long)decimal.Round(samplesPerPacketValue, 0, MidpointRounding.ToEven));
    if (samplesPerPacket <= 0 || Math.Abs(samplesPerPacketValue - samplesPerPacket) > 0.000001m)
      throw new MediaProbeException("ffprobe returned an unsupported packet duration.", []);

    var decodedSamples = checked(packetCount * samplesPerPacket - Gapless(first.Split(',')).SkipSamples - Gapless(last.Split(',')).DiscardPadding);
    if (decodedSamples <= 0) throw new MediaProbeException("ffprobe returned a non-positive decoded duration.", []);
    return checked((decimal)decodedSamples / track.SampleRate.Value);
  }

  private static (long SkipSamples, long DiscardPadding) Gapless(string[] fields)
  {
    if (fields.Length < 4 || !string.Equals(fields[1], "Skip Samples", StringComparison.OrdinalIgnoreCase)) return (0, 0);
    if (!long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var skip) || !long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var discard))
      throw new MediaProbeException("ffprobe returned malformed gapless evidence.", []);
    return (skip, discard);
  }

  private static AudioTrack ToAudio(FFprobeStream s) => new(s.Index, s.CodecName, s.CodecType, s.Channels, Int(s.SampleRate), Decimal(s.Duration), Rational(s.TimeBase), ToRawTags(s.Tags), s.Profile, s.ChannelLayout, Decimal(s.StartTime), s.CodecTag, s.ExtradataHash, Long(s.BitRate));
  private static TagCollection ToTags(Dictionary<string, JsonElement>? tags) => new(ToRawTags(tags));
  // Tag names are case-insensitive, and some files carry one twice (encoder and Encoder). The first non-empty value wins.
  private static Dictionary<string, string> ToRawTags(Dictionary<string, JsonElement>? tags)
  {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (key, element) in tags ?? [])
    {
      var value = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
      if (!result.TryGetValue(key, out var existing) || existing.Length == 0) result[key] = value;
    }
    return result;
  }
  private static int? Int(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
  private static long? Long(string? value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
  private static decimal? Decimal(string? value) => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
  private static decimal RequiredTimestamp(string? value, string field)
  {
    if (value is null) throw new MediaProbeException($"ffprobe returned a chapter without {field}.", []);
    if (Decimal(value) is { } decimalValue) return decimalValue;
    if (ParseRational(value) is { } rational) return (decimal)rational.ToDouble();
    throw new MediaProbeException($"ffprobe returned an invalid {field} value '{value}'.", []);
  }
  private static Rational? Rational(string? value) => value is null ? null : ParseRational(value);
  private static Rational RequiredRational(string? value) => value is null ? throw new MediaProbeException("ffprobe returned a chapter without time_base.", []) : ParseRational(value) ?? throw new MediaProbeException($"ffprobe returned an invalid time_base value '{value}'.", []);
  private static Rational? ParseRational(string value)
  { var p = value.Split('/'); if (p.Length != 2 || !long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || !long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) || d == 0) return null; return new Rational(n, d); }
}

public sealed class MediaProbeException : Exception
{
  public IReadOnlyList<string> Warnings { get; }
  public MediaProbeException(string message, IReadOnlyList<string> warnings, Exception? inner = null) : base(message, inner) => Warnings = warnings;
}
