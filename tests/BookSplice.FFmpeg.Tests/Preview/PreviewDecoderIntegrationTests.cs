using System.Security.Cryptography;
using BookSplice.Core.Preview;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Preview;
using BookSplice.FFmpeg.Tests.Execution;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.FFmpeg.Tests.Preview;

public sealed class PreviewDecoderIntegrationTests : IDisposable
{
  private const long Second = 1_000_000;
  private readonly string _root = Directory.CreateTempSubdirectory("booksplice-preview-").FullName;

  public void Dispose() => Directory.Delete(_root, true);

  [PinnedMediaFact]
  public async Task TrackWindowFromThreeSecondsDecodesNineSeconds()
  {
    var (tools, tracks, hashes) = await CreateBookAsync();
    var output = Path.Combine(_root, "out", "window.wav");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);

    await new PreviewDecoder(new ProcessRunner(), tools).DecodeAsync(PreviewPlanner.TrackWindow(tracks, 0, 3 * Second), output, CancellationToken.None);

    var seconds = Seconds(output);
    Assert.InRange(seconds, 9.0 - 0.03, 9.0 + 0.03);
    Assert.Equal(["window.wav"], Directory.GetFiles(Path.GetDirectoryName(output)!).Select(Path.GetFileName));
    AssertUnchanged(tracks, hashes);
  }

  [PinnedMediaFact]
  public async Task AroundBoundaryAcrossMp3AndAacDecodesTenSeconds()
  {
    var (tools, tracks, hashes) = await CreateBookAsync();
    var output = Path.Combine(_root, "boundary.wav");

    await new PreviewDecoder(new ProcessRunner(), tools).DecodeAsync(PreviewPlanner.AroundBoundary(tracks, 12 * Second), output, CancellationToken.None);

    var seconds = Seconds(output);
    Assert.InRange(seconds, 10.0 - 0.03, 10.0 + 0.03);
    AssertUnchanged(tracks, hashes);
  }

  [PinnedMediaFact]
  public async Task CancellationBeforeSecondPieceLeavesNoFiles()
  {
    var (tools, tracks, hashes) = await CreateBookAsync();
    var folder = Directory.CreateDirectory(Path.Combine(_root, "cancel")).FullName;
    var output = Path.Combine(folder, "cancelled.wav");
    using var cts = new CancellationTokenSource();
    var runner = new CancelAfterFirstRunner(new ProcessRunner(), cts);

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
      new PreviewDecoder(runner, tools).DecodeAsync(PreviewPlanner.AroundBoundary(tracks, 12 * Second), output, cts.Token));

    Assert.Equal(1, runner.Calls);
    Assert.Empty(Directory.GetFiles(folder));
    AssertUnchanged(tracks, hashes);
  }

  [PinnedMediaFact]
  public async Task NonAudioFileIsUnavailable()
  {
    var (tools, _, _) = await CreateBookAsync();
    var fake = Path.Combine(_root, "fake.mp3");
    await File.WriteAllTextAsync(fake, "this is not audio");
    var hash = Hash(fake);
    var output = Path.Combine(_root, "fake.wav");

    var exception = await Assert.ThrowsAsync<PreviewUnavailableException>(() =>
      new PreviewDecoder(new ProcessRunner(), tools).DecodeAsync(PreviewPlanner.TrackWindow([new PreviewTrack(fake, 10 * Second)], 0, 0), output, CancellationToken.None));

    Assert.Equal("This track can't be previewed.", exception.Message);
    Assert.False(File.Exists(output));
    Assert.Empty(Directory.GetFiles(_root, "fake.wav*"));
    Assert.Equal(hash, Hash(fake));
  }

  private async Task<(MediaToolSet Tools, PreviewTrack[] Tracks, string[] Hashes)> CreateBookAsync()
  {
    var directory = Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR");
    if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "ffmpeg.exe"))) throw Xunit.Sdk.SkipException.ForSkip("Pinned FFmpeg tools are not available.");
    var tools = new MediaToolLocator(directory).Resolve();
    var sources = Directory.CreateDirectory(Path.Combine(_root, "sources")).FullName;
    var mp3 = await GenerateAsync(tools, Path.Combine(sources, "01.mp3"), "libmp3lame", "440");
    var m4a = await GenerateAsync(tools, Path.Combine(sources, "02.m4a"), "aac", "550");
    PreviewTrack[] tracks = [new(mp3, 12 * Second), new(m4a, 12 * Second)];
    return (tools, tracks, tracks.Select(track => Hash(track.Path)).ToArray());
  }

  private static async Task<string> GenerateAsync(MediaToolSet tools, string path, string codec, string frequency)
  {
    var fixtureDirectory = codec == "libmp3lame" ? Environment.GetEnvironmentVariable("BOOKSPLICE_FIXTURE_FFMPEG_DIR") : null;
    var ffmpeg = string.IsNullOrWhiteSpace(fixtureDirectory) ? tools.FFmpegPath : new MediaToolLocator(fixtureDirectory).Resolve().FFmpegPath;
    string[] arguments = ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency={frequency}:duration=12", "-ac", "1", "-ar", "44100", "-c:a", codec, "-b:a", "96k", path];
    var result = await new ProcessRunner().RunAsync(new ProcessSpec(ffmpeg, arguments), null, CancellationToken.None);
    Assert.Equal(0, result.ExitCode);
    return path;
  }

  private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

  private static void AssertUnchanged(PreviewTrack[] tracks, string[] hashes) => Assert.Equal(hashes, tracks.Select(track => Hash(track.Path)));

  private static double Seconds(string wav)
  {
    var bytes = File.ReadAllBytes(wav);
    Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    return (bytes.Length - 44) / 4.0 / 44100.0;
  }

  private sealed class CancelAfterFirstRunner(IProcessRunner inner, CancellationTokenSource cts) : IProcessRunner
  {
    public int Calls { get; private set; }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken)
    {
      Calls++;
      var result = await inner.RunAsync(spec, progress, cancellationToken);
      cts.Cancel();
      return result;
    }
  }
}
