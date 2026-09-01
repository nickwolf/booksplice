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

  private static SourceFile File(string path, string codecName = "aac", string? profile = "LC", int sampleRate = 44100, string? extradataHash = "abc", string container = "mov,mp4,m4a,3gp,3g2,mj2")
    => new(path, path, new MediaProbeResult([
      new AudioTrack(0, codecName, "audio", 1, sampleRate, 1m, new Rational(1, sampleRate), new Dictionary<string, string>(), profile, "mono", 0m, "mp4a", extradataHash, 64000)
    ], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1m, container, 100));
}
