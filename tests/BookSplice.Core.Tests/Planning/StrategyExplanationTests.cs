using BookSplice.Core.Planning;

namespace BookSplice.Core.Tests.Planning;

public sealed class StrategyExplanationTests
{
  [Fact]
  public void CopyEligibleSaysNoReencode()
  {
    var explanation = StrategyExplanation.Create(AudioStrategy.AacStreamCopy, []);
    Assert.Contains("without re-encoding", explanation.Summary);
    Assert.Empty(explanation.Reasons);
  }

  [Fact]
  public void IncompatibleCodecIsNamed()
  {
    var explanation = StrategyExplanation.Create(AudioStrategy.FilterConcatTranscode, ["stream-copy.codec.unsupported", "stream-copy.container.unsupported"]);
    Assert.StartsWith("Audio will be re-encoded to AAC.", explanation.Summary);
    Assert.Equal(["The source audio is not AAC.", "A source file is not in an MP4-family container."], explanation.Reasons);
  }

  [Fact]
  public void ChannelPolicyIsExplainedAsASettingChange()
  {
    var explanation = StrategyExplanation.Create(AudioStrategy.DirectTranscode, ["stream-copy.channel-policy"]);
    Assert.Single(explanation.Reasons);
    Assert.Contains("channel setting", explanation.Reasons[0]);
  }

  [Fact]
  public void MismatchAcrossSourcesIsGrouped()
  {
    var explanation = StrategyExplanation.Create(AudioStrategy.FilterConcatTranscode, ["stream-copy.time-base.mismatch", "stream-copy.codec-tag.mismatch", "stream-copy.extradata-hash.mismatch", "stream-copy.sample-rate.mismatch"]);
    Assert.Equal(["The source files have different sample rates.", "The source files were encoded with different AAC settings."], explanation.Reasons);
  }

  [Fact]
  public void MissingEvidenceIsWordedAsNotConfirmedAndGrouped()
  {
    var explanation = StrategyExplanation.Create(AudioStrategy.DirectTranscode, ["stream-copy.profile.missing", "stream-copy.sample-rate.missing", "stream-copy.timestamp.missing"]);
    var reason = Assert.Single(explanation.Reasons);
    Assert.StartsWith("Could not confirm", reason);
    Assert.Contains("profile, sample rate, and start time", reason);
    Assert.DoesNotContain("not AAC", reason);
  }

  [Fact]
  public void SegmentLimitExplainsSegmentedEncodingNotAReason()
  {
    var explanation = StrategyExplanation.Create(AudioStrategy.SegmentedTranscode, ["stream-copy.codec.unsupported", "strategy.filter-concat-file-limit"]);
    Assert.Contains("32 files", explanation.Summary);
    Assert.Contains("separate segments", explanation.Summary);
    Assert.Equal(["The source audio is not AAC."], explanation.Reasons);
  }

  [Fact]
  public void UnknownCodeGetsGenericFallback()
  {
    var explanation = StrategyExplanation.Create(AudioStrategy.DirectTranscode, ["stream-copy.future-check", "something.else"]);
    var reason = Assert.Single(explanation.Reasons);
    Assert.DoesNotContain("future-check", reason);
  }

  [Theory]
  [InlineData(0L, "0 bytes")]
  [InlineData(1023L, "1023 bytes")]
  [InlineData(1536L, "1.5 KB")]
  [InlineData(431_800_000L, "411.8 MB")]
  [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
  public void FormatsBytes(long bytes, string expected) => Assert.Equal(expected, SpaceEstimateDescription.FormatBytes(bytes));

  [Fact]
  public void DescribesCombinedAndSeparateVolumes()
  {
    var combined = SpaceEstimateDescription.Lines(new SpaceEstimate(100, 100, 220, 5000, 4780, "D:\\"));
    Assert.Equal(["Estimated space needed: 220 bytes on D: for output and temporary files with a 10% margin (free space: 4.9 KB)"], combined);
    var separate = SpaceEstimateDescription.Lines(new SpaceEstimate(100, 200, 330, 5000, 4000, "D:\\", "C:\\", 110, 220, null));
    Assert.Equal(["Estimated output size: 100 bytes on D: (free space: 4.9 KB)", "Estimated temporary space: 200 bytes on C: (free space unavailable)"], separate);
  }
}
