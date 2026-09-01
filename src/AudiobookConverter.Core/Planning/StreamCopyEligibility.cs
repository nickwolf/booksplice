using AudiobookConverter.Core.Discovery;

namespace AudiobookConverter.Core.Planning;

public sealed class StreamCopyEligibility
{
  private StreamCopyEligibility(bool isEligible, IEnumerable<string> reasonCodes)
  {
    IsEligible = isEligible;
    ReasonCodes = Array.AsReadOnly(reasonCodes.Distinct(StringComparer.Ordinal).OrderBy(code => code, StringComparer.Ordinal).ToArray());
  }

  public bool IsEligible { get; }
  public IReadOnlyList<string> ReasonCodes { get; }

  public static StreamCopyEligibility Evaluate(IReadOnlyList<SourceFile> files)
  {
    ArgumentNullException.ThrowIfNull(files);
    var reasons = new List<string>();
    if (files.Count == 0) reasons.Add("stream-copy.no-sources");
    var reference = files.Count == 0 ? null : files[0].ProbeResult.AudioStreams.SingleOrDefault();
    foreach (var file in files)
    {
      var streams = file.ProbeResult.AudioStreams;
      if (streams.Count != 1) { reasons.Add("stream-copy.audio-stream-count"); continue; }
      var stream = streams[0];
      if (!string.Equals(stream.CodecName, "aac", StringComparison.OrdinalIgnoreCase)) reasons.Add("stream-copy.codec.unsupported");
      if (!string.Equals(stream.CodecProfile, "LC", StringComparison.OrdinalIgnoreCase) && !string.Equals(stream.CodecProfile, "AAC-LC", StringComparison.OrdinalIgnoreCase)) reasons.Add(string.IsNullOrWhiteSpace(stream.CodecProfile) ? "stream-copy.profile.missing" : "stream-copy.profile.unsupported");
      if (stream.Channels is not 1 and not 2) reasons.Add(stream.Channels is null ? "stream-copy.channel-count.missing" : "stream-copy.channel-count.unsupported");
      if (string.IsNullOrWhiteSpace(stream.ChannelLayout)) reasons.Add("stream-copy.channel-layout.missing");
      if (stream.SampleRate is not > 0) reasons.Add("stream-copy.sample-rate.missing");
      if (stream.TimeBase is not { Numerator: > 0, Denominator: > 0 }) reasons.Add("stream-copy.time-base.missing");
      if (string.IsNullOrWhiteSpace(stream.CodecTag)) reasons.Add("stream-copy.codec-tag.missing");
      if (string.IsNullOrWhiteSpace(stream.ExtradataSha256)) reasons.Add("stream-copy.extradata-hash.missing");
      if (stream.StartTime is null) reasons.Add("stream-copy.timestamp.missing");
      else if (stream.StartTime != 0) reasons.Add(stream.StartTime < 0 ? "stream-copy.timestamp.negative" : "stream-copy.timestamp.nonzero");
      if (!IsMp4Family(file.ProbeResult.FormatNames)) reasons.Add("stream-copy.container.unsupported");
      if (string.Equals(Path.GetExtension(file.FullPath), ".m4b", StringComparison.OrdinalIgnoreCase)) reasons.Add("stream-copy.existing-m4b");
      if (reference is not null && !ReferenceEquals(stream, reference)) AddMismatches(reference, stream, reasons);
    }
    return new StreamCopyEligibility(reasons.Count == 0, reasons);
  }

  private static bool IsMp4Family(string? names)
    => names?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(name => name is "mov" or "mp4" or "m4a" or "3gp" or "3g2" or "mj2") == true;

  private static void AddMismatches(Analysis.AudioTrack first, Analysis.AudioTrack candidate, List<string> reasons)
  {
    if (first.SampleRate != candidate.SampleRate) reasons.Add("stream-copy.sample-rate.mismatch");
    if (first.Channels != candidate.Channels) reasons.Add("stream-copy.channel-count.mismatch");
    if (!string.Equals(first.ChannelLayout, candidate.ChannelLayout, StringComparison.Ordinal)) reasons.Add("stream-copy.channel-layout.mismatch");
    if (first.TimeBase != candidate.TimeBase) reasons.Add("stream-copy.time-base.mismatch");
    if (!string.Equals(first.CodecTag, candidate.CodecTag, StringComparison.Ordinal)) reasons.Add("stream-copy.codec-tag.mismatch");
    if (!string.Equals(first.ExtradataSha256, candidate.ExtradataSha256, StringComparison.OrdinalIgnoreCase)) reasons.Add("stream-copy.extradata-hash.mismatch");
  }
}
