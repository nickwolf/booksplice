using System.Security.Cryptography;
using BookSplice.Core.Analysis;
using BookSplice.Core.Covers;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Tests.Execution;
using BookSplice.FFmpeg.Tools;
using BookSplice.FFmpeg.Validation;

namespace BookSplice.FFmpeg.Tests.Validation;

public sealed class CoverPayloadValidatorIntegrationTests
{
  [PinnedMediaFact]
  public async Task ValidateAsyncAcceptsTheExactAttachedCoverPayloadReadByTagLib()
  {
    using var root = new TemporaryDirectory();
    var tools = new MediaToolLocator(Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR")!).Resolve();
    var cover = root.GetPath("cover.jpg");
    var output = root.GetPath("book.m4b");
    var runner = new ProcessRunner();
    await RunAsync(runner, tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=32x32:d=1", "-frames:v", "1", "-c:v", "mjpeg", cover]);
    await RunAsync(runner, tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-i", cover, "-map", "0:a:0", "-map", "1:v:0", "-c:a", "aac", "-ar", "44100", "-ac", "1", "-c:v", "copy", "-disposition:v:0", "attached_pic", output]);
    var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(cover))).ToLowerInvariant();
    var plan = Plan(output, hash);
    var report = await new FFmpegOutputValidator(new FFprobeMediaProbe(runner, tools), runner, tools, new CoverPayloadValidator()).ValidateAsync(plan, output, CancellationToken.None);

    Assert.True(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "cover.payload" && check.Passed);
  }

  private static async Task RunAsync(ProcessRunner runner, string fileName, IReadOnlyList<string> arguments)
  {
    var result = await runner.RunAsync(new ProcessSpec(fileName, arguments), null, CancellationToken.None);
    Assert.Equal(0, result.ExitCode);
  }

  private static ConversionPlan Plan(string output, string coverHash)
  {
    var cover = new CoverCandidate(coverHash, CoverOrigin.ExternalFile, "cover.jpg", null, "image/jpeg", 32, 32, CoverSemanticType.FrontCover, true);
    return new ConversionPlan(["source.mp3"], new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()), cover, [], QualityProfileCatalog.Version1[0], ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, output, AudioStrategy.DirectTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 1, "test", "GenericMp4", 44_100, 1);
  }

  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("booksplice-cover-validation-").FullName;
    public string Path { get; }
    public string GetPath(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
  }
}
