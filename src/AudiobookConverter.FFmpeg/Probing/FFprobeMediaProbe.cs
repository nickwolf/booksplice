using System.Globalization;
using System.Text.Json;
using AudiobookConverter.Core.Analysis;
using AudiobookConverter.FFmpeg.Execution;
using AudiobookConverter.FFmpeg.Tools;

namespace AudiobookConverter.FFmpeg.Probing;

public sealed class FFprobeMediaProbe : IMediaProbe
{
  private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
  private readonly IProcessRunner _runner;
  private readonly MediaToolSet _tools;
  public FFprobeMediaProbe(IProcessRunner runner, MediaToolSet tools) { _runner = runner; _tools = tools; }

  public async Task<MediaProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken = default)
  {
    var spec = new ProcessSpec(_tools.FFprobePath, ["-v", "warning", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", "-show_error", inputPath]);
    var result = await _runner.RunAsync(spec, null, cancellationToken).ConfigureAwait(false);
    var warnings = result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToArray();
    if (result.ExitCode != 0) throw new MediaProbeException($"ffprobe failed with exit code {result.ExitCode}.", warnings);
    FFprobeDocument document;
    try { document = JsonSerializer.Deserialize<FFprobeDocument>(result.StandardOutput, JsonOptions) ?? throw new JsonException("empty document"); }
    catch (JsonException exception) { throw new MediaProbeException("ffprobe returned malformed JSON.", warnings, exception); }
    var formatTags = ToTags(document.Format?.Tags);
    var audio = (document.Streams ?? []).Where(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)).Select(ToAudio).ToArray();
    var covers = (document.Streams ?? []).Where(s => s.Disposition?.AttachedPic == 1).Select(s => new MediaAttachedPicture(s.Index, s.CodecName, s.Width, s.Height, ToRawTags(s.Tags))).ToArray();
    var chapters = (document.Chapters ?? []).Select(c => new MediaChapter(c.Id, RequiredTimestamp(c.StartTime, "start_time"), RequiredTimestamp(c.EndTime, "end_time"), RequiredRational(c.TimeBase), ToRawTags(c.Tags))).ToArray();
    return new MediaProbeResult(audio, covers, chapters, formatTags, ToRawTags(document.Format?.Tags), warnings, Decimal(document.Format?.Duration));
  }
  private static AudioTrack ToAudio(FFprobeStream s) => new(s.Index, s.CodecName, s.CodecType, s.Channels, Int(s.SampleRate), Decimal(s.Duration), Rational(s.TimeBase), ToRawTags(s.Tags));
  private static TagCollection ToTags(Dictionary<string, JsonElement>? tags) => new(ToRawTags(tags));
  private static Dictionary<string, string> ToRawTags(Dictionary<string, JsonElement>? tags) => (tags ?? []).ToDictionary(p => p.Key, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString(), StringComparer.Ordinal);
  private static int? Int(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
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
