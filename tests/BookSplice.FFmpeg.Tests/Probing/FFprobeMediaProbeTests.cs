using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.FFmpeg.Tests.Probing;

public sealed class FFprobeMediaProbeTests
{
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
