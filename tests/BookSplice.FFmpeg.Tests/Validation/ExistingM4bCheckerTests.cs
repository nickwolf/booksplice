using BookSplice.Core.Analysis;
using BookSplice.Core.Validation;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tools;
using BookSplice.FFmpeg.Validation;

namespace BookSplice.FFmpeg.Tests.Validation;

public sealed class ExistingM4bCheckerTests
{
  [Fact]
  public async Task GoodFileFailsNothingAndMarksPlanOnlyChecksNotChecked()
  {
    using var file = new TemporaryFile();
    var report = await Checker(Facts(), new Runner()).CheckAsync(file.Path, false, CancellationToken.None);

    Assert.True(report.IsValid);
    Assert.DoesNotContain(report.Checks, check => check.Status == ExistingCheckStatus.Failed);
    foreach (var code in new[] { "audio.sample-rate", "audio.layout", "duration.expected", "chapters.count", "chapters.timing", "chapters.titles", "cover.presence", "cover.payload", "metadata.required", "decode.full" })
      Assert.Contains(report.Checks, check => check.Code == code && check.Status == ExistingCheckStatus.NotChecked && !check.Required);
    Assert.Equal("Needs the original tracks.", report.Checks.Single(check => check.Code == "audio.layout").Message);
    Assert.Contains(report.NotEstablished, line => line.Contains("originals aren't available", StringComparison.Ordinal));
    Assert.NotNull(report.Facts);
  }

  [Fact]
  public async Task MissingFileFailsWithoutProbing()
  {
    var report = await Checker(Facts(), new Runner()).CheckAsync(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "booksplice-missing.m4b"), false, CancellationToken.None);

    Assert.False(report.IsValid);
    Assert.Equal(["file.exists"], report.Checks.Select(check => check.Code));
    Assert.Contains(report.NotEstablished, line => line.Contains("originals", StringComparison.Ordinal));
  }

  [Fact]
  public async Task UnparsableFileFailsContainerParse()
  {
    using var file = new TemporaryFile();
    var report = await new ExistingM4bChecker(new FailingProbe(), new Runner(), Tools).CheckAsync(file.Path, false, CancellationToken.None);

    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "container.parse" && check.Status == ExistingCheckStatus.Failed);
  }

  [Fact]
  public async Task UnreadablePacketEndFails()
  {
    using var file = new TemporaryFile();
    var report = await Checker(Facts(), new Runner { PacketExitCode = 1 }).CheckAsync(file.Path, false, CancellationToken.None);

    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "packets.end-region" && check.Status == ExistingCheckStatus.Failed && check.Required);
  }

  [Fact]
  public async Task ChapterEndingPastAudioFails()
  {
    using var file = new TemporaryFile();
    var report = await Checker(Facts(chapters: [new MediaChapter(0, 0m, 1m, null, Tags()), new MediaChapter(1, 1m, 5m, null, Tags())]), new Runner()).CheckAsync(file.Path, false, CancellationToken.None);

    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "chapters.within-audio" && check.Status == ExistingCheckStatus.Failed);
    Assert.Contains(report.Checks, check => check.Code == "chapters.monotonic" && check.Status == ExistingCheckStatus.Passed);
  }

  [Fact]
  public async Task OverlappingOrZeroLengthChaptersFailWithOneClearMessage()
  {
    using var file = new TemporaryFile();
    var overlap = await Checker(Facts(chapters: [new MediaChapter(0, 0m, 1.5m, null, Tags()), new MediaChapter(1, 1m, 2m, null, Tags())]), new Runner()).CheckAsync(file.Path, false, CancellationToken.None);
    var zero = await Checker(Facts(chapters: [new MediaChapter(0, 0m, 1m, null, Tags()), new MediaChapter(1, 1m, 1m, null, Tags())]), new Runner()).CheckAsync(file.Path, false, CancellationToken.None);

    foreach (var report in new[] { overlap, zero })
    {
      Assert.False(report.IsValid);
      var failed = Assert.Single(report.Checks, check => check.Code == "chapters.monotonic");
      Assert.Equal(ExistingCheckStatus.Failed, failed.Status);
      Assert.Contains("overlap", failed.Message, StringComparison.Ordinal);
    }
  }

  [Fact]
  public async Task CoverAndMissingTagsAreNotesAndNeverFail()
  {
    using var file = new TemporaryFile();
    var withCover = await Checker(Facts(cover: true, tags: false), new Runner()).CheckAsync(file.Path, false, CancellationToken.None);
    var without = await Checker(Facts(cover: false, tags: true), new Runner()).CheckAsync(file.Path, false, CancellationToken.None);

    Assert.True(withCover.IsValid);
    Assert.True(without.IsValid);
    Assert.Contains(withCover.Checks, check => check.Code == "cover.found" && check.Status == ExistingCheckStatus.Note && check.Message == "Cover: mjpeg 500x500");
    Assert.Contains(withCover.Checks, check => check.Code == "tags.title" && check.Status == ExistingCheckStatus.Note);
    Assert.Contains(withCover.Checks, check => check.Code == "tags.author" && check.Status == ExistingCheckStatus.Note);
    Assert.Contains(without.Checks, check => check.Code == "cover.found" && check.Message == "No cover.");
    Assert.DoesNotContain(without.Checks, check => check.Code is "tags.title" or "tags.author");
    Assert.All(withCover.Checks.Concat(without.Checks).Where(check => check.Status == ExistingCheckStatus.Note), check => Assert.False(check.Required));
  }

  [Fact]
  public async Task WrongCodecAndExtraAudioFail()
  {
    using var file = new TemporaryFile();
    var mp3 = new AudioTrack(0, "mp3", "audio", 1, 44_100, 3m, new Rational(1, 44_100), Tags(), null, "mono");
    var report = await Checker(Facts(audio: [mp3, mp3 with { Index = 1 }]), new Runner()).CheckAsync(file.Path, false, CancellationToken.None);

    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "audio.primary-count" && check.Status == ExistingCheckStatus.Failed);
    Assert.Contains(report.Checks, check => check.Code == "audio.extra" && check.Status == ExistingCheckStatus.Failed);
  }

  [Fact]
  public async Task FullDecodeRunsOnlyWhenRequestedAndMapsErrors()
  {
    using var file = new TemporaryFile();
    var skipped = new Runner();
    var off = await Checker(Facts(), skipped).CheckAsync(file.Path, false, CancellationToken.None);
    var okRunner = new Runner();
    var on = await Checker(Facts(), okRunner).CheckAsync(file.Path, true, CancellationToken.None);
    var bad = await Checker(Facts(), new Runner { DecodeStandardError = "[aac] Invalid data found" }).CheckAsync(file.Path, true, CancellationToken.None);

    Assert.Equal(0, skipped.DecodeCalls);
    Assert.Contains(off.Checks, check => check.Code == "decode.full" && check.Status == ExistingCheckStatus.NotChecked);
    Assert.Equal(1, okRunner.DecodeCalls);
    Assert.Contains(on.Checks, check => check.Code == "decode.full" && check.Status == ExistingCheckStatus.Passed);
    Assert.False(bad.IsValid);
    Assert.Contains(bad.Checks, check => check.Code == "decode.full" && check.Status == ExistingCheckStatus.Failed);
  }

  [Fact]
  public async Task CancellationPropagates()
  {
    using var file = new TemporaryFile();
    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Checker(Facts(), new Runner()).CheckAsync(file.Path, true, cancellation.Token));
  }

  private static readonly MediaToolSet Tools = new("ffmpeg.exe", "ffprobe.exe", "pinned", "pinned");

  private static ExistingM4bChecker Checker(MediaProbeResult facts, IProcessRunner runner) => new(new Probe(facts), runner, Tools);

  private static Dictionary<string, string> Tags() => [];

  private static MediaProbeResult Facts(IReadOnlyList<AudioTrack>? audio = null, IReadOnlyList<MediaChapter>? chapters = null, bool cover = false, bool tags = true)
    => new(audio ?? [new AudioTrack(0, "aac", "audio", 1, 44_100, 3m, new Rational(1, 44_100), Tags(), "LC", "mono")],
      cover ? [new MediaAttachedPicture(1, "mjpeg", 500, 500, Tags())] : [],
      chapters ?? [new MediaChapter(0, 0m, 1.5m, null, Tags()), new MediaChapter(1, 1.5m, 3m, null, Tags())],
      new TagCollection(tags ? [KeyValuePair.Create("title", "Book"), KeyValuePair.Create("artist", "Author")] : []),
      Tags(), [], 3m);

  private sealed class Probe(MediaProbeResult facts) : IMediaProbe
  {
    public Task<MediaProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken = default) => Task.FromResult(facts);
  }

  private sealed class FailingProbe : IMediaProbe
  {
    public Task<MediaProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken = default) => throw new InvalidOperationException("not an mp4");
  }

  private sealed class Runner : IProcessRunner
  {
    public int PacketExitCode { get; init; }
    public string DecodeStandardError { get; init; } = "";
    public int DecodeCalls { get; private set; }

    public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (spec.Arguments.Contains("-show_packets")) return Task.FromResult(new ProcessResult(PacketExitCode, "audio,1,2", ""));
      DecodeCalls++;
      return Task.FromResult(new ProcessResult(0, "", DecodeStandardError));
    }
  }

  private sealed class TemporaryFile : IDisposable
  {
    public TemporaryFile() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"booksplice-check-{Guid.NewGuid():N}.m4b"); File.WriteAllBytes(Path, [0]); }
    public string Path { get; }
    public void Dispose() { try { File.Delete(Path); } catch { } }
  }
}
