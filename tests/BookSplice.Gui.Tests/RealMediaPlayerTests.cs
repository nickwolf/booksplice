using System.IO;
using BookSplice.Core.Preview;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Preview;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.Gui.Tests;

public class PinnedMediaFactAttribute : FactAttribute
{
  public PinnedMediaFactAttribute()
  {
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR")))
      Skip = "Pinned FFmpeg tools are not available; set BOOKSPLICE_FFMPEG_DIR to run real-media tests.";
  }
}

// MediaPlayer fails with 0xC00D11BA when there is no audio output device, as on GitHub's Windows runners.
public sealed class AudioDeviceFactAttribute : PinnedMediaFactAttribute
{
  public AudioDeviceFactAttribute()
  {
    if (Skip is null && WaveOutGetNumDevs() == 0) Skip = "This machine has no audio output device.";
  }

  [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "waveOutGetNumDevs")]
  private static extern uint WaveOutGetNumDevs();
}

// Proves that the WPF MediaPlayer opens the WAV the preview decoder writes, and lets go of it on Close.
public sealed class RealMediaPlayerTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("booksplice-player-media-").FullName;

  public void Dispose() => Directory.Delete(_root, true);

  [AudioDeviceFact]
  public void TheRealPlayerOpensTheWavTheRealDecoderWritesAndReleasesItOnClose() => DispatcherTestHost.Run(async _ =>
  {
    var directory = Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR");
    if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "ffmpeg.exe"))) throw Xunit.Sdk.SkipException.ForSkip("Pinned FFmpeg tools are not available.");
    var tools = new MediaToolLocator(directory).Resolve();
    var source = Path.Combine(_root, "01.m4a");
    string[] arguments = ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=6", "-ac", "1", "-ar", "44100", "-c:a", "aac", "-b:a", "96k", source];
    var generated = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFmpegPath, arguments), null, CancellationToken.None);
    Assert.Equal(0, generated.ExitCode);
    var wav = Path.Combine(_root, "window.wav");
    await new PreviewDecoder(new ProcessRunner(), tools).DecodeAsync(PreviewPlanner.TrackWindow([new PreviewTrack(source, 6_000_000)], 0, 1_000_000), wav, CancellationToken.None);

    var opened = new TaskCompletionSource();
    var failed = new TaskCompletionSource<string>();
    using var player = new MediaPlayerAudioPlayer();
    player.Opened += (_, _) => opened.TrySetResult();
    player.Failed += (_, message) => failed.TrySetResult(message);
    player.Open(wav);

    var first = await Task.WhenAny(opened.Task, failed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    Assert.True(ReferenceEquals(first, opened.Task), failed.Task.IsCompleted ? $"The player failed: {failed.Task.Result}" : "The player neither opened the file nor failed within 5 seconds.");

    player.Close();
    // MediaPlayer can hold the file for a moment after Close. Anything still locked after two seconds is a real failure.
    for (var attempt = 0; attempt < 20 && File.Exists(wav); attempt++)
    {
      try { File.Delete(wav); }
      catch (IOException) { await Task.Delay(100); }
    }
    Assert.False(File.Exists(wav));
  });
}
