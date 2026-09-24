using System.Buffers.Binary;
using System.Text;
using BookSplice.Core.Analysis;
using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.FFmpeg.Tests.Probing;

public sealed class FFprobeMediaProbeTests
{
  [Theory]
  [InlineData(0, 1)]
  [InlineData(255, 256)]
  public void NeroReaderDefersToMoreCompleteNativeChapterTrack(int neroCount, int nativeCount)
  {
    var path = Path.Combine(Path.GetTempPath(), $"booksplice-chpl-{Guid.NewGuid():N}.m4b");
    try
    {
      var payload = new byte[9 + neroCount * 9];
      payload[8] = checked((byte)neroCount);
      for (var index = 0; index < neroCount; index++)
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(9 + index * 9, 8), checked((ulong)index * 10_000_000));
      File.WriteAllBytes(path, Box("moov", Box("udta", Box("chpl", payload))));
      var native = Enumerable.Range(0, nativeCount).Select(index => new MediaChapter(index, index, index + 1, new Rational(1, 1), new Dictionary<string, string> { ["title"] = $"Chapter {index}" })).ToArray();

      Assert.Null(NeroChapterReader.Read(path, nativeCount, native));
    }
    finally { if (File.Exists(path)) File.Delete(path); }
  }
  [Fact]
  public async Task ProbeAsyncMapsAudioCoverTagsAndChapters()
  {
    var runner = new FixtureRunner(File.ReadAllText(FixturePath));
    var probe = new FFprobeMediaProbe(runner, new MediaToolSet("ffmpeg", "ffprobe", "ffmpeg version x", "ffprobe version x"));

    var result = await probe.ProbeAsync(FixturePath, CancellationToken.None);

    Assert.Equal(["-v", "warning", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", "-show_data_hash", "sha256", "-show_error", FixturePath], runner.LastSpec!.Arguments);

    Assert.Single(result.AudioStreams);
    Assert.Equal("aac", result.AudioStreams[0]!.CodecName);
    Assert.Single(result.AttachedPictures);
    Assert.Equal(2, result.Chapters.Count);
    Assert.Equal("Example Book", result.FormatTags["album"]);
    Assert.Equal("Example Book", result.FormatTags.GetValue("ALBUM"));
    Assert.Equal("A\nB", result.RawTags["Artist"]);
    Assert.Equal(1, result.AudioStreams[0]!.TimeBase!.Numerator);
    Assert.Equal(44100, result.AudioStreams[0]!.TimeBase!.Denominator);
    Assert.Equal("LC", result.AudioStreams[0]!.CodecProfile);
    Assert.Equal("stereo", result.AudioStreams[0]!.ChannelLayout);
    Assert.Equal("mp4a", result.AudioStreams[0]!.CodecTag);
    Assert.Equal("SHA256:abc", result.AudioStreams[0]!.ExtradataSha256);
    Assert.Equal(64000, result.AudioStreams[0]!.BitRate);
    Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", result.FormatNames);
    Assert.Equal(1000, result.SourceByteSize);
  }

  [Fact]
  public async Task ProbeAsyncUsesMp3PacketCountAndGaplessPaddingForDuration()
  {
    var runner = new SequenceRunner(
      """{"streams":[{"index":0,"codec_name":"mp3","codec_type":"audio","sample_rate":"44100","duration":"99","start_time":"0","time_base":"1/14112000"}],"format":{"duration":"99","format_name":"mp3"}}""",
      """{"streams":[{"sample_rate":"44100","time_base":"1/14112000","nb_read_packets":"308"}]}""",
      """{"packets":[{"duration":368640,"side_data_list":[{"side_data_type":"Skip Samples","skip_samples":1105,"discard_padding":0}]}]}""",
      """{"packets":[{"duration":368640,"side_data_list":[{"side_data_type":"Skip Samples","skip_samples":0,"discard_padding":911}]}]}""");
    var probe = new FFprobeMediaProbe(runner, new MediaToolSet("ffmpeg", "ffprobe", "", ""));

    var result = await probe.ProbeAsync("generated.mp3", CancellationToken.None);

    Assert.Equal(8m, result.Duration);
    Assert.Equal(8m, result.AudioStreams.Single().Duration);
    Assert.Equal(4, runner.Specs.Count);
    Assert.Contains("-count_packets", runner.Specs[1].Arguments);
    Assert.Contains("%+#1", runner.Specs[2].Arguments);
    Assert.Contains("-1%+#1000", runner.Specs[3].Arguments);
  }
  [Fact]
  public async Task ProbeAsyncSubtractsDiscardPaddingWhenFirstPacketHasNoSkipSamples()
  {
    var runner = new SequenceRunner(
      """{"streams":[{"index":0,"codec_name":"mp3","codec_type":"audio","sample_rate":"44100","duration":"99","start_time":"0","time_base":"1/14112000"}],"format":{"duration":"99","format_name":"mp3"}}""",
      """{"streams":[{"sample_rate":"44100","time_base":"1/14112000","nb_read_packets":"100"}]}""",
      """{"packets":[{"duration":368640}]}""",
      """{"packets":[{"duration":368640,"side_data_list":[{"side_data_type":"Skip Samples","skip_samples":0,"discard_padding":576}]}]}""");
    var probe = new FFprobeMediaProbe(runner, new MediaToolSet("ffmpeg", "ffprobe", "", ""));

    var result = await probe.ProbeAsync("generated.mp3", CancellationToken.None);

    Assert.Equal(114_624m / 44_100m, result.Duration);
    Assert.Equal(4, runner.Specs.Count);
    Assert.Contains("-1%+#1000", runner.Specs[3].Arguments);
  }
  [Fact]
  public async Task ProbeAsyncRetainsWarningsAndReportsNonzeroExit()
  {
    var runner = new FixtureRunner("{}", 7, "warning: metadata\n");
    var probe = new FFprobeMediaProbe(runner, new MediaToolSet("ffmpeg", "ffprobe", "", ""));

    var exception = await Assert.ThrowsAsync<MediaProbeException>(() => probe.ProbeAsync("input with spaces-😀.m4b", CancellationToken.None));

    Assert.Contains("exit code 7", exception.Message);
    Assert.Contains("metadata", exception.Warnings.Single());
    Assert.Equal("input with spaces-😀.m4b", runner.LastSpec!.Arguments[^1]);
  }

  [Fact]
  public async Task ProbeAsyncRejectsMalformedJson()
  {
    var runner = new FixtureRunner("not json");
    var probe = new FFprobeMediaProbe(runner, new MediaToolSet("", "ffprobe", "", ""));
    await Assert.ThrowsAsync<MediaProbeException>(() => probe.ProbeAsync("-leading-hyphen", CancellationToken.None));
    Assert.Equal("-leading-hyphen", runner.LastSpec!.Arguments[^1]);
  }

  [Fact]
  public async Task ProbeAsyncRejectsMalformedChapterTimeData()
  {
    var runner = new FixtureRunner("{\"chapters\":[{\"id\":1,\"start_time\":\"bad\",\"end_time\":\"2\",\"time_base\":\"1/0\"}]}");
    var probe = new FFprobeMediaProbe(runner, new MediaToolSet("", "ffprobe", "", ""));
    var exception = await Assert.ThrowsAsync<MediaProbeException>(() => probe.ProbeAsync("book\n\"quoted\".m4b", CancellationToken.None));
    Assert.Contains("start_time", exception.Message);
    Assert.Equal("book\n\"quoted\".m4b", runner.LastSpec!.Arguments[^1]);
  }

  [Fact]
  public async Task ProbeAsyncRejectsZeroDenominatorChapterRational()
  {
    var runner = new FixtureRunner("{\"chapters\":[{\"start_time\":\"0\",\"end_time\":\"2\",\"time_base\":\"1/0\"}]}");
    var exception = await Assert.ThrowsAsync<MediaProbeException>(() => new FFprobeMediaProbe(runner, new MediaToolSet("", "ffprobe", "", "")).ProbeAsync("book", CancellationToken.None));
    Assert.Contains("time_base", exception.Message);
  }

  [Fact]
  public async Task ProbeAsyncRejectsNullRequiredChapterTime()
  {
    var runner = new FixtureRunner("{\"chapters\":[{\"start_time\":null,\"end_time\":\"2\",\"time_base\":\"1/1\"}]}");
    var exception = await Assert.ThrowsAsync<MediaProbeException>(() => new FFprobeMediaProbe(runner, new MediaToolSet("", "ffprobe", "", "")).ProbeAsync("book", CancellationToken.None));
    Assert.Contains("start_time", exception.Message);
  }

  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ffprobe-complete.json");
  private static byte[] Box(string type, byte[] payload)
  {
    var bytes = new byte[8 + payload.Length];
    BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)bytes.Length));
    Encoding.ASCII.GetBytes(type, bytes.AsSpan(4, 4));
    payload.CopyTo(bytes, 8);
    return bytes;
  }

  private sealed class SequenceRunner(params string[] outputs) : IProcessRunner
  {
    private int _index;
    public List<ProcessSpec> Specs { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken)
    {
      Specs.Add(spec);
      if (_index >= outputs.Length) throw new InvalidOperationException("The probe made an unexpected process call.");
      return Task.FromResult(new ProcessResult(0, outputs[_index++], ""));
    }
  }
  private sealed class FixtureRunner(string output, int exitCode = 0, string error = "") : IProcessRunner
  {
    public ProcessSpec? LastSpec { get; private set; }
    public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken)
    {
      LastSpec = spec;
      return Task.FromResult(new ProcessResult(exitCode, output, error));
    }
  }
}
