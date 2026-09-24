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
    if (audio.Length == 1 && string.Equals(audio[0].CodecName, "mp3", StringComparison.OrdinalIgnoreCase))
    {
      duration = await GetExactMp3DurationAsync(inputPath, cancellationToken).ConfigureAwait(false);
      audio[0] = audio[0] with { Duration = duration };
    }
    return new MediaProbeResult(audio, covers, chapters, formatTags, ToRawTags(document.Format?.Tags), warnings, duration, document.Format?.FormatName, Long(document.Format?.Size));
  }

  private async Task<decimal> GetExactMp3DurationAsync(string inputPath, CancellationToken cancellationToken)
  {
    var count = await RunJsonAsync<FFprobeDocument>(
      ["-v", "error", "-select_streams", "a:0", "-count_packets", "-show_entries", "stream=nb_read_packets,sample_rate,time_base", "-print_format", "json", inputPath],
      cancellationToken).ConfigureAwait(false);
    var stream = count.Streams?.SingleOrDefault() ?? throw new MediaProbeException("ffprobe did not return MP3 packet-count evidence.", []);
    var packetCount = Long(stream.NbReadPackets);
    var sampleRate = Int(stream.SampleRate);
    var timeBase = Rational(stream.TimeBase);
    if (packetCount is not > 0 || sampleRate is not > 0 || timeBase is null)
      throw new MediaProbeException("ffprobe returned incomplete MP3 packet-count evidence.", []);

    var first = await ReadPacketAsync(inputPath, "%+#1", takeLast: false, cancellationToken).ConfigureAwait(false);
    var packetDuration = first.Duration;
    if (packetDuration is not > 0) throw new MediaProbeException("ffprobe omitted the MP3 packet duration.", []);
    var samplesPerPacketValue = checked((decimal)packetDuration.Value * timeBase.Numerator * sampleRate.Value / timeBase.Denominator);
    var samplesPerPacket = checked((long)decimal.Round(samplesPerPacketValue, 0, MidpointRounding.ToEven));
    if (samplesPerPacket <= 0 || Math.Abs(samplesPerPacketValue - samplesPerPacket) > 0.000001m)
      throw new MediaProbeException("ffprobe returned an unsupported MP3 packet duration.", []);

    var skipSamples = Gapless(first).SkipSamples;
    var last = await ReadPacketAsync(inputPath, "-1%+#1000", takeLast: true, cancellationToken).ConfigureAwait(false);
    var discardPadding = Gapless(last).DiscardPadding;
    var decodedSamples = checked(packetCount.Value * samplesPerPacket - skipSamples - discardPadding);
    if (decodedSamples <= 0) throw new MediaProbeException("ffprobe returned a non-positive MP3 decoded duration.", []);
    return checked((decimal)decodedSamples / sampleRate.Value);
  }

  private async Task<FFprobePacket> ReadPacketAsync(string inputPath, string interval, bool takeLast, CancellationToken cancellationToken)
  {
    var document = await RunJsonAsync<FFprobePacketDocument>(
      ["-v", "error", "-select_streams", "a:0", "-read_intervals", interval, "-show_packets", "-show_entries", "packet=duration,side_data_list", "-print_format", "json", inputPath],
      cancellationToken).ConfigureAwait(false);
    var packets = document.Packets ?? [];
    if (packets.Count == 0) throw new MediaProbeException("ffprobe did not return MP3 packet boundary evidence.", []);
    return takeLast ? packets[^1] : packets[0];
  }

  private async Task<T> RunJsonAsync<T>(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
  {
    var result = await _runner.RunAsync(new ProcessSpec(_tools.FFprobePath, arguments), null, cancellationToken).ConfigureAwait(false);
    if (result.ExitCode != 0) throw new MediaProbeException("ffprobe could not read exact MP3 duration evidence.", result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    try { return JsonSerializer.Deserialize<T>(result.StandardOutput, JsonOptions) ?? throw new JsonException("empty document"); }
    catch (JsonException exception) { throw new MediaProbeException("ffprobe returned malformed MP3 duration evidence.", [], exception); }
  }

  private static (long SkipSamples, long DiscardPadding) Gapless(FFprobePacket packet)
  {
    var evidence = packet.SideData?.SingleOrDefault(item => string.Equals(item.Type, "Skip Samples", StringComparison.OrdinalIgnoreCase));
    return evidence is null ? (0, 0) : (evidence.SkipSamples, evidence.DiscardPadding);
  }

  private static AudioTrack ToAudio(FFprobeStream s) => new(s.Index, s.CodecName, s.CodecType, s.Channels, Int(s.SampleRate), Decimal(s.Duration), Rational(s.TimeBase), ToRawTags(s.Tags), s.Profile, s.ChannelLayout, Decimal(s.StartTime), s.CodecTag, s.ExtradataHash, Long(s.BitRate));
  private static TagCollection ToTags(Dictionary<string, JsonElement>? tags) => new(ToRawTags(tags));
  private static Dictionary<string, string> ToRawTags(Dictionary<string, JsonElement>? tags) => (tags ?? []).ToDictionary(p => p.Key, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString(), StringComparer.Ordinal);
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
