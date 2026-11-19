using BookSplice.FFmpeg.Commands;
using BookSplice.FFmpeg.Execution;

namespace BookSplice.FFmpeg.Tests.Execution;

public sealed class SegmentedPipelineUnitTests
{
  private const int Frame = SegmentRangePlanner.FrameSamples;

  [Theory]
  [InlineData("[out#0/null @ 000001]   Output stream #0:0 (audio): 41 frames encoded (45626 samples); 41 packets muxed (91252 bytes); ", 45626)]
  [InlineData("noise\n[out#0/null @ 0000]   Output stream #0:0 (audio): 1 frames encoded (0 samples); 1 packets muxed (2 bytes); \nmore", 0)]
  public void SampleCountComesFromTheEncoderSummary(string standardError, long expected)
  {
    Assert.Equal(expected, SourceSampleCounter.Parse(standardError, out var summary));
    Assert.StartsWith("[out#0/null", summary, StringComparison.Ordinal);
    Assert.DoesNotContain('\n', summary);
  }

  [Theory]
  [InlineData("")]
  [InlineData("Input stream #0:0 (audio): 41 packets read (8568 bytes); 41 frames decoded; 0 decode errors (45626 samples); ")]
  [InlineData("Output stream #0:0 (audio): 41 frames encoded (N/A samples)")]
  public void SummaryThatIsMissingOrUnreadableFailsTheMeasurement(string standardError)
    => Assert.Throws<SegmentedAssemblyException>(() => SourceSampleCounter.Parse(standardError, out _));

  [Fact]
  public void RangesStartOnTheFrameGridAndTileTheTimelineExactly()
  {
    var counts = Enumerable.Range(0, 40).Select(index => 400_000L + 137 * index).ToArray();
    var ranges = SegmentRangePlanner.Plan(counts, 3);
    var total = counts.Sum();
    Assert.Equal(6, ranges.Count);
    Assert.Equal(0, ranges[0].Start);
    Assert.Equal(total, ranges[^1].End);
    Assert.All(ranges.Zip(ranges.Skip(1)), pair => Assert.Equal(pair.First.End, pair.Second.Start));
    Assert.All(ranges.Take(ranges.Count - 1), range => Assert.Equal(0, range.End % Frame));
    Assert.All(ranges, range => { Assert.Equal(0, range.ExtendedStart % Frame); Assert.True(range.End - range.Start >= SegmentRangePlanner.MinimumFrames * Frame); });
    Assert.Equal(ranges.Select(range => range.Index), Enumerable.Range(0, ranges.Count));
    Assert.All(ranges, range => Assert.InRange(range.End - range.Start, total / 6 - Frame, total / 6 + Frame));
  }

  [Fact]
  public void ExtendedRangesCoverThePaddingAndClampAtTheEnds()
  {
    var ranges = SegmentRangePlanner.Plan([3_000_000], 1);
    Assert.Equal(0, ranges[0].ExtendedStart);
    Assert.Equal(ranges[0].End + SegmentRangePlanner.PaddingSamples, ranges[0].ExtendedEnd);
    Assert.Equal(ranges[1].Start - SegmentRangePlanner.PaddingSamples, ranges[1].ExtendedStart);
    Assert.Equal(3_000_000, ranges[1].ExtendedEnd);
    Assert.Equal(ranges[1].ExtendedStart, ranges[1].TrimStart);
    Assert.Equal(ranges[1].ExtendedEnd, ranges[1].TrimEnd);
  }

  [Fact]
  public void NoRangeNeedsMoreThanThirtyTwoInputsWhenItCanBeSplitFurther()
  {
    var counts = Enumerable.Repeat(44_100L, 300).ToArray();
    var ranges = SegmentRangePlanner.Plan(counts, 2);
    Assert.True(ranges.Count > 4);
    Assert.All(ranges, range => Assert.InRange(range.Inputs.Count, 1, SegmentRangePlanner.MaximumInputs));
    Assert.All(ranges, range => Assert.Equal(range.ExtendedStart - range.Inputs[0] * 44_100L, range.TrimStart));
  }

  [Fact]
  public void ARangeThatCannotBeSplitFurtherMayExceedTheInputLimit()
  {
    var ranges = SegmentRangePlanner.Plan(Enumerable.Repeat(100L, 2000).ToArray(), 2);
    Assert.All(ranges, range => Assert.True(range.End - range.Start < 2 * SegmentRangePlanner.MinimumFrames * Frame));
    Assert.Contains(ranges, range => range.Inputs.Count > SegmentRangePlanner.MaximumInputs);
  }

  [Fact]
  public void InputsAreTheFilesOverlappingTheExtendedRangeAndSkipEmptyOnes()
  {
    var ranges = SegmentRangePlanner.Plan([30_000, 0, 30_000, 30_000], 1);
    var range = Assert.Single(ranges);
    Assert.Equal([0, 2, 3], range.Inputs);
    Assert.True(range.IsFirst && range.IsLast);
  }

  [Theory]
  [InlineData(1)]
  [InlineData(1023)]
  [InlineData(1024)]
  [InlineData(70_000)]
  public void TinyBooksGetOneRangeKeepingEveryFrame(long total)
  {
    var range = Assert.Single(SegmentRangePlanner.Plan([total], 8));
    Assert.Equal((0, total), (range.Start, range.End));
    Assert.Equal(0, range.FirstKeptFrame);
    Assert.Equal((total + Frame - 1) / Frame, range.LastKeptFrame - 0);
    Assert.Equal(range.ExpectedFrames - 1, range.LastKeptFrame);
  }

  [Fact]
  public void KeptFramesTileTheEncodedStreamWithoutGapOrOverlap()
  {
    var counts = Enumerable.Range(0, 40).Select(index => 400_000L + 137 * index).ToArray();
    var ranges = SegmentRangePlanner.Plan(counts, 3);
    var kept = 0;
    foreach (var range in ranges)
    {
      Assert.Equal(range.IsFirst ? 0 : range.Start / Frame + 1, kept);
      kept += range.LastKeptFrame - range.FirstKeptFrame + 1;
    }
    Assert.Equal((int)((counts.Sum() + Frame - 1) / Frame) + 1, kept);
    var middle = ranges[2];
    Assert.Equal((int)((middle.Start - middle.ExtendedStart) / Frame) + 1, middle.FirstKeptFrame);
    Assert.Equal((int)((middle.End - middle.ExtendedStart) / Frame), middle.LastKeptFrame);
  }

  [Fact]
  public void NoSamplesIsAnError() => Assert.Throws<ArgumentException>(() => SegmentRangePlanner.Plan([0, 0], 2));

  [Fact]
  public void AdtsFramesAreReadAndCopiedByIndex()
  {
    using var directory = new TemporaryFolder();
    var path = Path.Combine(directory.Path, "a.aac");
    File.WriteAllBytes(path, SegmentedFakeMedia.Adts(10));
    var lengths = AdtsFrames.ReadLengths(path);
    Assert.Equal(Enumerable.Repeat(16, 10), lengths);
    using var output = new MemoryStream();
    AdtsFrames.Append(path, lengths, 3, 5, output);
    Assert.Equal(SegmentedFakeMedia.Adts(10)[(3 * 16)..(6 * 16)], output.ToArray());
  }

  [Fact]
  public void AdtsLengthComesFromTheThirteenBitFieldAcrossThreeBytes()
  {
    byte[] header = [0xFF, 0xF1, 0x50, 0x80 | 0x02, 0xA5, 0xC0 | 0x1F, 0xFC];
    Assert.Equal((2 << 11) | (0xA5 << 3) | 6, AdtsFrames.Length(header));
  }

  [Theory]
  [InlineData("sync")]
  [InlineData("short-length")]
  [InlineData("truncated-header")]
  [InlineData("runs-past-end")]
  public void CorruptAdtsIsRejected(string kind)
  {
    using var directory = new TemporaryFolder();
    var path = Path.Combine(directory.Path, "bad.aac");
    var bytes = SegmentedFakeMedia.Adts(3);
    switch (kind)
    {
      case "sync": bytes[16] = 0x00; break;
      case "short-length": bytes[16 + 4] = 0; bytes[16 + 5] = 0x1F; bytes[16 + 3] = 0x40; break;
      case "truncated-header": bytes = [.. bytes, 0xFF, 0xF1]; break;
      case "runs-past-end": bytes = bytes[..^4]; break;
    }
    File.WriteAllBytes(path, bytes);
    Assert.Throws<SegmentedAssemblyException>(() => AdtsFrames.ReadLengths(path));
  }

  [Fact]
  public void AppendRejectsFramesOutsideTheSegment()
  {
    using var directory = new TemporaryFolder();
    var path = Path.Combine(directory.Path, "a.aac");
    File.WriteAllBytes(path, SegmentedFakeMedia.Adts(4));
    using var output = new MemoryStream();
    Assert.Throws<SegmentedAssemblyException>(() => AdtsFrames.Append(path, AdtsFrames.ReadLengths(path), 2, 4, output));
  }

  [Theory]
  [InlineData(0, 44_100u, 1L)]
  [InlineData(1, 28_224_000u, 640L)]
  public void EditListPatchSetsTheStartAndExactLengthInPlace(int version, uint movieScale, long ticksPerSample)
  {
    using var directory = new TemporaryFolder();
    var path = Path.Combine(directory.Path, "i.m4a");
    var original = SegmentedFakeMedia.Mp4(version, movieScale, 44_100, 123, 0);
    File.WriteAllBytes(path, original);

    Mp4MetadataBoxes.PatchSingleEditList(path, 1024, 100_000, 44_100);

    var patched = File.ReadAllBytes(path);
    Assert.Equal(original.Length, patched.Length);
    Assert.Equal((100_000L * ticksPerSample, 1024L), SegmentedFakeMedia.ReadElst(patched, version));
    Assert.True(original.Zip(patched).Count(pair => pair.First != pair.Second) <= (version == 1 ? 16 : 8));
  }

  [Fact]
  public void EditListDurationUsesTheMovieTimescaleAndRoundsToNearest()
  {
    using var directory = new TemporaryFolder();
    var path = Path.Combine(directory.Path, "i.m4a");
    File.WriteAllBytes(path, SegmentedFakeMedia.Mp4(0, 1000, 44_100, 1, 0));
    Mp4MetadataBoxes.PatchSingleEditList(path, 1024, 441_050, 44_100);
    Assert.Equal((10_001L, 1024L), SegmentedFakeMedia.ReadElst(File.ReadAllBytes(path), 0));
  }

  [Fact]
  public void EditListPatchRefusesFilesItCannotPatchExactly()
  {
    using var directory = new TemporaryFolder();
    var path = Path.Combine(directory.Path, "i.m4a");
    File.WriteAllBytes(path, [0, 0, 0, 8, (byte)'f', (byte)'t', (byte)'y', (byte)'p']);
    Assert.Throws<InvalidDataException>(() => Mp4MetadataBoxes.PatchSingleEditList(path, 1024, 1000, 44_100));
    File.WriteAllBytes(path, SegmentedFakeMedia.Mp4(0, 1000, 44_100, 1, 0));
    Assert.Throws<InvalidDataException>(() => Mp4MetadataBoxes.PatchSingleEditList(path, 1000, 1000, 48_000));
  }

  private sealed class TemporaryFolder : IDisposable
  {
    public string Path { get; } = Directory.CreateTempSubdirectory().FullName;
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
  }
}
