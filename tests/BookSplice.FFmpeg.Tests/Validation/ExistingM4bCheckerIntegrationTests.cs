using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using BookSplice.Core.Execution;
using BookSplice.Core.Validation;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Tests.Execution;
using BookSplice.FFmpeg.Tools;
using BookSplice.FFmpeg.Validation;

namespace BookSplice.FFmpeg.Tests.Validation;

public sealed class ExistingM4bCheckerIntegrationTests
{
  [PinnedMediaFact]
  public async Task ValidFileWithChaptersAndCoverPassesAndIsLeftUntouched()
  {
    using var root = new TemporaryDirectory();
    var tools = Tools();
    var path = await GenerateAsync(root, tools, cover: true);
    var before = Hash(path);

    var report = await Checker(tools).CheckAsync(path, true, CancellationToken.None);

    Assert.True(report.IsValid, Describe(report));
    Assert.Equal(3, report.Facts!.Chapters.Count);
    Assert.Contains(report.Checks, check => check.Code == "cover.found" && check.Status == ExistingCheckStatus.Note && check.Message == "Cover: mjpeg 64x64");
    Assert.Contains(report.Checks, check => check.Code == "decode.full" && check.Status == ExistingCheckStatus.Passed);
    Assert.Contains(report.Checks, check => check.Code == "chapters.within-audio" && check.Status == ExistingCheckStatus.Passed);
    // The chapter text track makes ffmpeg print "Unsupported codec with id 98314", which says nothing about the file.
    Assert.DoesNotContain(report.Warnings, warning => warning.Contains("98314", StringComparison.Ordinal));
    Assert.Equal(before, Hash(path));
  }

  [PinnedMediaFact]
  public async Task TruncatedFileFailsAndIsLeftUntouched()
  {
    using var root = new TemporaryDirectory();
    var tools = Tools();
    var path = await GenerateAsync(root, tools, cover: true);
    var bytes = await File.ReadAllBytesAsync(path);
    await File.WriteAllBytesAsync(path, bytes.AsMemory(0, bytes.Length * 7 / 10));
    var before = Hash(path);

    var report = await Checker(tools).CheckAsync(path, false, CancellationToken.None);

    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Status == ExistingCheckStatus.Failed && check.Required);
    Assert.Equal(before, Hash(path));
  }

  [PinnedMediaFact]
  public async Task ChaptersRunningPastTheAudioFailAndTheFileIsLeftUntouched()
  {
    using var root = new TemporaryDirectory();
    var tools = Tools();
    var path = await GenerateAsync(root, tools, cover: true, lastChapterEndMilliseconds: 9_000);
    var before = Hash(path);

    var report = await Checker(tools).CheckAsync(path, false, CancellationToken.None);

    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "chapters.within-audio" && check.Status == ExistingCheckStatus.Failed);
    Assert.Equal(before, Hash(path));
  }

  [PinnedMediaFact]
  public async Task FileWithoutCoverIsValidWithANote()
  {
    using var root = new TemporaryDirectory();
    var tools = Tools();
    var path = await GenerateAsync(root, tools, cover: false);
    var before = Hash(path);

    var report = await Checker(tools).CheckAsync(path, false, CancellationToken.None);

    Assert.True(report.IsValid, Describe(report));
    Assert.Contains(report.Checks, check => check.Code == "cover.found" && check.Status == ExistingCheckStatus.Note && check.Message == "No cover.");
    Assert.Equal(before, Hash(path));
  }

  [PinnedMediaFact]
  public async Task OverwrittenAudioBytesFailTheFullDecodeOnlyWhenRequested()
  {
    using var root = new TemporaryDirectory();
    var tools = Tools();
    var path = await GenerateAsync(root, tools, cover: true);
    var bytes = await File.ReadAllBytesAsync(path);
    var (start, length) = FindMediaData(bytes);
    bytes.AsSpan(start + length / 2, Math.Min(512, length / 4)).Fill(0xff);
    await File.WriteAllBytesAsync(path, bytes);
    var before = Hash(path);

    var full = await Checker(tools).CheckAsync(path, true, CancellationToken.None);
    var quick = await Checker(tools).CheckAsync(path, false, CancellationToken.None);

    Assert.False(full.IsValid);
    Assert.Contains(full.Checks, check => check.Code == "decode.full" && check.Status == ExistingCheckStatus.Failed);
    Assert.DoesNotContain(quick.Checks, check => check.Code == "decode.full" && check.Status != ExistingCheckStatus.NotChecked);
    Assert.Equal(before, Hash(path));
  }

  [PinnedMediaFact]
  public async Task CancelDuringFullDecodeThrowsAndLeavesTheFileUntouched()
  {
    using var root = new TemporaryDirectory();
    var tools = Tools();
    var path = await GenerateAsync(root, tools, cover: true, seconds: 30);
    var before = Hash(path);
    using var cancellation = new CancellationTokenSource();
    var progress = new SyncProgress(value => { if (value.Phase == ConversionPhase.FullDecode) cancellation.Cancel(); });

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Checker(tools).CheckAsync(path, true, cancellation.Token, progress));

    Assert.Equal(before, Hash(path));
  }

  [PinnedMediaFact]
  public async Task CancelOnceTheDecodeIsRunningStopsItEarlyAndLeavesTheFileUntouched()
  {
    using var root = new TemporaryDirectory();
    var tools = Tools();
    var seed = await GenerateAsync(root, tools, cover: false, seconds: 600);
    var path = root.GetPath("long.m4b");
    var looped = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-v", "error", "-y", "-stream_loop", "35", "-i", seed, "-map", "0:a:0", "-c", "copy", path]), null, CancellationToken.None);
    Assert.Equal(0, looped.ExitCode);
    var before = Hash(path);
    // Time the decode itself: the probe and packet passes before it can take most of the check on a busy runner.
    var decodeStartedAt = TimeSpan.Zero;
    var uncancelled = Stopwatch.StartNew();
    var baseline = await Checker(tools).CheckAsync(path, true, CancellationToken.None, new SyncProgress(value =>
    {
      if (IsDecoding(value) && decodeStartedAt == TimeSpan.Zero) decodeStartedAt = uncancelled.Elapsed;
    }));
    uncancelled.Stop();
    var decode = uncancelled.Elapsed - decodeStartedAt;
    Assert.Contains(baseline.Checks, check => check.Code == "decode.full" && check.Status == ExistingCheckStatus.Passed);

    using var cancellation = new CancellationTokenSource();
    var cancelledAt = TimeSpan.Zero;
    var watch = Stopwatch.StartNew();
    var progress = new SyncProgress(value =>
    {
      if (IsDecoding(value) && !cancellation.IsCancellationRequested)
      {
        cancelledAt = watch.Elapsed;
        cancellation.Cancel();
      }
    });

    var cancelled = false;
    try { await Checker(tools).CheckAsync(path, true, cancellation.Token, progress); }
    catch (OperationCanceledException) { cancelled = true; }
    watch.Stop();

    var timings = $"Uncancelled check took {uncancelled.Elapsed.TotalSeconds:F1}s with the decode starting at {decodeStartedAt.TotalSeconds:F1}s; cancel requested at {cancelledAt.TotalSeconds:F1}s, cancelled run took {watch.Elapsed.TotalSeconds:F1}s.";
    Assert.True(decodeStartedAt > TimeSpan.Zero && cancelledAt > TimeSpan.Zero, "No decode progress was reported. " + timings);
    Assert.True(cancelled, "The check finished before the cancel took effect. " + timings);
    Assert.True(watch.Elapsed - cancelledAt < decode / 2, timings);
    Assert.Equal(before, Hash(path));
  }

  private static bool IsDecoding(ConversionProgress value) => value.Phase == ConversionPhase.FullDecode && value.PhasePercent is > 0;

  private static (int Start, int Length) FindMediaData(byte[] bytes)
  {
    for (var offset = 0; offset + 8 <= bytes.Length;)
    {
      var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));
      Assert.InRange(size, 8, bytes.Length - offset);
      if (bytes.AsSpan(offset + 4, 4).SequenceEqual("mdat"u8)) return (offset + 8, size - 8);
      offset += size;
    }
    throw new InvalidOperationException("No mdat box was found.");
  }

  private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

  private static string Describe(ExistingFileCheckReport report) => string.Join(Environment.NewLine, report.Checks.Where(check => check.Status == ExistingCheckStatus.Failed).Select(check => $"{check.Code}: {check.Message}"));

  private static MediaToolSet Tools() => new MediaToolLocator(Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR")!).Resolve();

  private static ExistingM4bChecker Checker(MediaToolSet tools)
  {
    var runner = new ProcessRunner();
    return new(new FFprobeMediaProbe(runner, tools), runner, tools);
  }

  private static async Task<string> GenerateAsync(TemporaryDirectory root, MediaToolSet tools, bool cover, int seconds = 6, int? lastChapterEndMilliseconds = null)
  {
    var output = root.GetPath("book.m4b");
    var metadata = root.GetPath("chapters.txt");
    var third = seconds * 1000;
    var last = lastChapterEndMilliseconds ?? third;
    var first = third / 3;
    var second = third * 2 / 3;
    await File.WriteAllTextAsync(metadata, $";FFMETADATA1\ntitle=Test Book\nartist=Test Author\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND={first}\ntitle=One\n[CHAPTER]\nTIMEBASE=1/1000\nSTART={first}\nEND={second}\ntitle=Two\n[CHAPTER]\nTIMEBASE=1/1000\nSTART={second}\nEND={last}\ntitle=Three\n");
    var runner = new ProcessRunner();
    var coverPath = root.GetPath("cover.jpg");
    if (cover)
    {
      var image = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=64x64:d=1", "-frames:v", "1", "-c:v", "mjpeg", coverPath]), null, CancellationToken.None);
      Assert.Equal(0, image.ExitCode);
    }
    string[] input = ["-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}", "-f", "ffmetadata", "-i", metadata];
    string[] coverArgs = cover ? ["-i", coverPath, "-map", "2:v:0", "-c:v", "copy", "-disposition:v:0", "attached_pic"] : [];
    var generated = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-v", "error", "-y", .. input, .. coverArgs, "-map", "0:a:0", "-map_metadata", "1", "-map_chapters", "1", "-c:a", "aac", "-ar", "44100", "-ac", "1", output]), null, CancellationToken.None);
    Assert.Equal(0, generated.ExitCode);
    return output;
  }

  private sealed class SyncProgress(Action<ConversionProgress> action) : IProgress<ConversionProgress>
  {
    public void Report(ConversionProgress value) => action(value);
  }

  private sealed class TemporaryDirectory : IDisposable
  {
    private readonly string _root = Directory.CreateTempSubdirectory("booksplice-check-test-").FullName;
    public string GetPath(string name) => Path.Combine(_root, name);
    public void Dispose()
    {
      if (!Path.GetFileName(_root).StartsWith("booksplice-check-test-", StringComparison.Ordinal)) throw new IOException("Test cleanup escaped the owned temporary directory.");
      Directory.Delete(_root, true);
    }
  }
}
