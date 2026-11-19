using System.Globalization;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.FFmpeg.Tests.Execution;

public sealed class AacEncoderFrameContractTests
{
  [PinnedMediaFact]
  public async Task EncoderEmitsOneFrameOfPrimingAndFramesForEverySample()
  {
    var tools = ResolveTools();
    var root = Directory.CreateTempSubdirectory("booksplice-adts-frames-").FullName;
    try
    {
      foreach (var rate in new[] { 44_100, 22_050 })
        foreach (var samples in new[] { 1, 1023, 1024, 1025, 3 * 1024, 100_003 })
        {
          var path = Path.Combine(root, $"frames-{rate}-{samples}.aac");
          var seconds = (samples / (double)rate).ToString("F9", CultureInfo.InvariantCulture);
          var made = await Run(tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate={rate}:duration={seconds}", "-af", $"atrim=end_sample={samples},asetpts=N/SR/TB", "-c:a", "aac", "-b:a", "64k", "-f", "adts", path]);
          Assert.Equal(0, made.ExitCode);
          Assert.Equal((samples + 1023) / 1024 + 1, AdtsFrames.ReadLengths(path).Count);
        }
    }
    finally { Directory.Delete(root, true); }
  }

  private static Task<ProcessResult> Run(string executable, IEnumerable<string> arguments) => new ProcessRunner().RunAsync(new ProcessSpec(executable, arguments.ToArray()), null, CancellationToken.None);

  private static MediaToolSet ResolveTools()
  {
    var directory = Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR");
    if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "ffmpeg.exe"))) throw Xunit.Sdk.SkipException.ForSkip("Pinned FFmpeg tools are not available; set BOOKSPLICE_FFMPEG_DIR to run real-media integration tests.");
    return new MediaToolLocator(directory).Resolve();
  }
}
