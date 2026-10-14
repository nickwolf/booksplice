using System.Buffers.Binary;
using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.Core.Validation;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Tests.Execution;
using BookSplice.FFmpeg.Tools;
using BookSplice.FFmpeg.Validation;

namespace BookSplice.FFmpeg.Tests.Validation;

public sealed class ResultValidationIntegrationTests
{
  [PinnedMediaFact]
  public async Task ValidChapterDataTrackRetainsProbeWarningAndPassesFullAudioDecode()
  {
    using var root = new TemporaryDirectory();
    var tools = Tools();
    var output = await GenerateAsync(root, tools);
    var report = await Validator(tools).ValidateAsync(Plan(output), output, CancellationToken.None);
    Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Errors));
    Assert.Single(report.OutputFacts!.AudioStreams);
    Assert.Contains(report.OutputFacts.OtherStreams!, stream => stream.CodecType == "data");
    Assert.Contains(report.Warnings, warning => warning.Contains("Unsupported codec", StringComparison.Ordinal));
    Assert.Contains(report.Checks, check => check.Code == "decode.full" && check.Passed);
  }

  [PinnedMediaFact]
  public async Task MissingRequiredAudioIsAValidationFailure()
  {
    using var root = new TemporaryDirectory();
    var tools = Tools();
    var output = root.GetPath("missing-audio.m4b");
    var generated = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFmpegPath,
      ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=32x32:d=1", "-frames:v", "1", "-c:v", "mjpeg", "-f", "mp4", output]), null, CancellationToken.None);
    Assert.Equal(0, generated.ExitCode);
    var report = await Validator(tools).ValidateAsync(Plan(output, chapters: false), output, CancellationToken.None);
    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "audio.primary-count" && !check.Passed);
    Assert.Contains(report.Checks, check => check.Code == "decode.full" && !check.Passed);
  }

  [PinnedMediaFact]
  public async Task CorruptAudioRetainsDecoderErrorsAlongsideUnsupportedDataTrackWarning()
  {
    using var root = new TemporaryDirectory();
    var tools = Tools();
    var output = await GenerateAsync(root, tools);
    var bytes = await File.ReadAllBytesAsync(output);
    var foundMedia = false;
    for (var offset = 0; offset + 8 <= bytes.Length;)
    {
      var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));
      Assert.InRange(size, 8, bytes.Length - offset);
      if (bytes.AsSpan(offset + 4, 4).SequenceEqual("mdat"u8))
      {
        bytes.AsSpan(offset + 8, Math.Min(256, size - 8)).Fill(0xff);
        foundMedia = true;
        break;
      }
      offset += size;
    }
    Assert.True(foundMedia);
    await File.WriteAllBytesAsync(output, bytes);
    var report = await Validator(tools).ValidateAsync(Plan(output), output, CancellationToken.None);
    Assert.False(report.IsValid);
    Assert.Contains(report.Checks, check => check.Code == "decode.full" && !check.Passed);
    Assert.Contains(report.Errors, error => error.Contains("decode", StringComparison.Ordinal));
    Assert.Contains(report.Warnings, warning => warning.Contains("Unsupported codec", StringComparison.Ordinal));
    Assert.Contains(report.Warnings, warning => warning.Contains("Error", StringComparison.OrdinalIgnoreCase) || warning.Contains("Invalid data", StringComparison.OrdinalIgnoreCase));
  }

  private static MediaToolSet Tools() => new MediaToolLocator(Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR")!).Resolve();
  private static FFmpegOutputValidator Validator(MediaToolSet tools)
  {
    var runner = new ProcessRunner();
    return new(new FFprobeMediaProbe(runner, tools), runner, tools, new CoverPayloadValidator());
  }

  private static async Task<string> GenerateAsync(TemporaryDirectory root, MediaToolSet tools)
  {
    var output = root.GetPath("generated.m4b");
    var metadata = root.GetPath("chapters.txt");
    await File.WriteAllTextAsync(metadata, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=2000\ntitle=One\n");
    var generated = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFmpegPath,
      ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-f", "ffmetadata", "-i", metadata,
        "-map", "0:a:0", "-map_metadata", "1", "-map_chapters", "1", "-c:a", "aac", "-ar", "44100", "-ac", "1", output]), null, CancellationToken.None);
    Assert.Equal(0, generated.ExitCode);
    return output;
  }

  private static ConversionPlan Plan(string output, bool chapters = true)
    => new(["source.wav"], new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()),
      null, chapters ? [new ChapterEntry(0, 2_000_000, "One", "", "source.wav")] : [], QualityProfileCatalog.Version1[0], ValidationLevel.Full,
      CollisionPolicy.AvoidCollision, output, AudioStrategy.DirectTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 1, "test", "GenericMp4", 44100, 1);

  private sealed class TemporaryDirectory : IDisposable
  {
    private readonly string _root = Directory.CreateTempSubdirectory("booksplice-result-test-").FullName;
    public string GetPath(string name) => Path.Combine(_root, name);
    public void Dispose()
    {
      var relative = Path.GetRelativePath(Path.GetTempPath(), _root);
      if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal) || relative.Contains(Path.DirectorySeparatorChar) ||
          !relative.StartsWith("booksplice-result-test-", StringComparison.Ordinal)) throw new IOException("Test cleanup escaped the owned temporary directory.");
      if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
        .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)) throw new IOException("Test cleanup encountered a reparse point.");
      Directory.Delete(_root, true);
    }
  }
}
