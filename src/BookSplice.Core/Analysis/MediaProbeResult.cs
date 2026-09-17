namespace BookSplice.Core.Analysis;

public sealed record Rational(long Numerator, long Denominator)
{
  public double ToDouble() => Denominator == 0 ? 0 : (double)Numerator / Denominator;
}

public sealed record AudioTrack(
  int Index,
  string? CodecName,
  string? CodecType,
  int? Channels,
  int? SampleRate,
  decimal? Duration,
  Rational? TimeBase,
  IReadOnlyDictionary<string, string> RawTags,
  string? CodecProfile = null,
  string? ChannelLayout = null,
  decimal? StartTime = null,
  string? CodecTag = null,
  string? ExtradataSha256 = null,
  long? BitRate = null);

public sealed record MediaAttachedPicture(
  int Index,
  string? CodecName,
  int? Width,
  int? Height,
  IReadOnlyDictionary<string, string> RawTags);

public sealed record MediaChapter(
  long Id,
  decimal StartTime,
  decimal EndTime,
  Rational? TimeBase,
  IReadOnlyDictionary<string, string> RawTags);

public sealed class TagCollection : IReadOnlyDictionary<string, string>
{
  private readonly Dictionary<string, string> _values;
  public TagCollection(IEnumerable<KeyValuePair<string, string>> values)
    => _values = new(values, StringComparer.OrdinalIgnoreCase);
  public string GetValue(string key) => _values[key];
  public IEnumerable<string> Keys => _values.Keys;
  public IEnumerable<string> Values => _values.Values;
  public int Count => _values.Count;
  public string this[string key] => _values[key];
  public bool ContainsKey(string key) => _values.ContainsKey(key);
  public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
  public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();
  System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed record MediaProbeResult(
  IReadOnlyList<AudioTrack> AudioStreams,
  IReadOnlyList<MediaAttachedPicture> AttachedPictures,
  IReadOnlyList<MediaChapter> Chapters,
  TagCollection FormatTags,
  IReadOnlyDictionary<string, string> RawTags,
  IReadOnlyList<string> Warnings,
  decimal? Duration = null,
  string? FormatNames = null,
  long? SourceByteSize = null);

public interface IMediaProbe
{
  Task<MediaProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken = default);
}
