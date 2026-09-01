using AudiobookConverter.Core.Analysis;
using AudiobookConverter.Core.Discovery;
using AudiobookConverter.Core.Planning;

namespace AudiobookConverter.Core.Tests.Planning;

public sealed class StreamCopyEligibilityTests
{
  [Fact]
  public void Evaluate_accepts_matching_aac_lc_mp4_sources()
  {
    var result = StreamCopyEligibility.Evaluate([File("01.m4a"), File("02.m4a")]);

    Assert.True(result.IsEligible);
    Assert.Empty(result.ReasonCodes);
  }

  [Fact]
  public void Evaluate_accumulates_missing_and_mismatched_evidence_in_stable_order()
  {
    var result = StreamCopyEligibility.Evaluate([
      File("01.m4a"),
      File("02.m4a", codecName: "mp3", profile: null, sampleRate: 48000, extradataHash: null, container: "matroska")
    ]);

    Assert.False(result.IsEligible);
    Assert.Equal(["stream-copy.codec.unsupported", "stream-copy.container.unsupported", "stream-copy.extradata-hash.mismatch", "stream-copy.extradata-hash.missing", "stream-copy.profile.missing", "stream-copy.sample-rate.mismatch", "stream-copy.time-base.mismatch"], result.ReasonCodes);
  }

  [Fact]
  public void Evaluate_reports_a_stable_reason_for_every_load_bearing_evidence_dimension()
  {
    var cases = new (IReadOnlyList<SourceFile> Files, string Reason)[]
    {
      ([new SourceFile("a.m4a", "a.m4a", new MediaProbeResult([], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1m, "mp4", 1))], "stream-copy.audio-stream-count"),
      ([File("a.m4a", codecName: "mp3")], "stream-copy.codec.unsupported"),
      ([File("a.m4a", profile: null)], "stream-copy.profile.missing"),
      ([File("a.m4a", sampleRate: 0)], "stream-copy.sample-rate.missing"),
      ([File("a.m4a", channels: 3)], "stream-copy.channel-count.unsupported"),
      ([File("a.m4a", layout: null)], "stream-copy.channel-layout.missing"),
      ([File("a.m4a", timeBase: new Rational(0, 1))], "stream-copy.time-base.missing"),
      ([File("a.m4a", tag: null)], "stream-copy.codec-tag.missing"),
      ([File("a.m4a", extradataHash: null)], "stream-copy.extradata-hash.missing"),
      ([File("a.m4a", start: null)], "stream-copy.timestamp.missing"),
      ([File("a.m4a", start: -1m)], "stream-copy.timestamp.negative"),
      ([File("a.m4a", start: 1m)], "stream-copy.timestamp.nonzero"),
      ([File("a.m4a", container: "matroska")], "stream-copy.container.unsupported"),
      ([File("a.m4b")], "stream-copy.existing-m4b")
    };

    foreach (var test in cases)
    {
      var result = StreamCopyEligibility.Evaluate(test.Files);
      Assert.Contains(test.Reason, result.ReasonCodes);
      Assert.Equal(result.ReasonCodes.OrderBy(code => code, StringComparer.Ordinal), result.ReasonCodes);
    }
  }

  [Fact]
  public void Evaluate_requires_zero_timestamps_for_every_source()
  {
    var result = StreamCopyEligibility.Evaluate([File("a.m4a"), File("b.m4a", start: 1m)]);
    Assert.Contains("stream-copy.timestamp.nonzero", result.ReasonCodes);
  }

  private static SourceFile File(string path, string codecName = "aac", string? profile = "LC", int sampleRate = 44100, string? extradataHash = "abc", string container = "mov,mp4,m4a,3gp,3g2,mj2", int? channels = 1, string? layout = "mono", Rational? timeBase = null, string? tag = "mp4a", decimal? start = 0m)
    => new(path, path, new MediaProbeResult([
      new AudioTrack(0, codecName, "audio", channels, sampleRate, 1m, timeBase ?? new Rational(1, sampleRate), new Dictionary<string, string>(), profile, layout, start, tag, extradataHash, 64000)
    ], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1m, container, 100));
}
