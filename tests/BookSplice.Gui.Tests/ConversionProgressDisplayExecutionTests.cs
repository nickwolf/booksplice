using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.FFmpeg.Commands;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tools;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class ConversionProgressDisplayExecutionTests
{
  [Fact]
  public async Task SegmentedJobWithoutChaptersKeepsEncodingUntilAssemblyAndMetadata()
  {
    var root = Directory.CreateTempSubdirectory("booksplice-progress-").FullName;
    try
    {
      var sources = Enumerable.Range(0, 33).Select(index => Path.Combine(root, $"source-{index:D2}.mp3")).ToArray();
      var metadata = new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(),
        new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
      var plan = new ConversionPlan(sources, metadata, null, [], QualityProfileCatalog.Version1[2],
        ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, Path.Combine(root, "finished.m4b"),
        AudioStrategy.SegmentedTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 1, "test", MetadataProfiles.GenericMp4,
        sourceDurationsMicroseconds: Enumerable.Repeat<long?>(5_000_000, 33).ToArray());
      var executor = new FFmpegConversionExecutor(new ReportingRunner(),
        new FFmpegCommandFactory(new MediaToolSet("ffmpeg.exe", "ffprobe.exe", "pinned", "pinned")),
        new MetadataWriter(), Path.Combine(root, "jobs"));
      var display = new ConversionProgressDisplay();
      string? whileLastSegmentEncodes = null;
      double? whileLastSegmentPercent = null;
      var result = await executor.ExecuteAsync(plan, CancellationToken.None, new CallbackProgress(value =>
      {
        display.Report(value, TimeSpan.FromSeconds(30));
        if (value.SourceIndex == 1 && value.OutTimeMicroseconds is > 0 && value.State is null)
        {
          whileLastSegmentEncodes = display.Describe(TimeSpan.FromSeconds(30));
          whileLastSegmentPercent = display.Snapshot(TimeSpan.FromSeconds(30)).Percent;
        }
      }));

      Assert.Equal(ExecutionStatus.Succeeded, result.Status);
      Assert.Equal(100, whileLastSegmentPercent);
      Assert.NotNull(whileLastSegmentEncodes);
      Assert.Contains("Encoding audio", whileLastSegmentEncodes, StringComparison.Ordinal);
      Assert.DoesNotContain("Conversion complete", whileLastSegmentEncodes, StringComparison.Ordinal);
      Assert.Equal("Writing metadata", display.PhaseLabel);
      Assert.Null(display.Snapshot(TimeSpan.FromSeconds(30)).Percent);
    }
    finally { Directory.Delete(root, true); }
  }

  // Answers the segmented pipeline's commands without ffmpeg: a fixed length per source, ADTS segments of the contract's size, and an MP4 with one edit list.
  private sealed class ReportingRunner : IProcessRunner
  {
    private const long SamplesPerSource = 220_500;

    public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var arguments = spec.Arguments.ToList();
      var standardError = string.Empty;
      if (arguments.Contains("-itsoffset")) File.WriteAllBytes(arguments[^1], Mp4());
      else if (arguments.Contains("null")) standardError = $"Output stream #0:0 (audio): 200 frames encoded ({SamplesPerSource} samples); 200 packets muxed (1 bytes); ";
      else if (arguments.Contains("adts"))
      {
        var match = System.Text.RegularExpressions.Regex.Match(arguments[arguments.IndexOf("-filter_complex") + 1], @"start_sample=(\d+):end_sample=(\d+)");
        var samples = long.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) - long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllBytes(arguments[^1], Adts((int)((samples + 1023) / 1024) + 1));
        progress?.Report($"out_time_us={samples * 1_000_000 / 44_100}");
        progress?.Report("speed=12.3x");
        progress?.Report("progress=continue");
      }
      else
      {
        progress?.Report("out_time_us=5000000");
        progress?.Report("speed=12.3x");
        progress?.Report("progress=continue");
      }
      return Task.FromResult(new ProcessResult(0, string.Empty, standardError));
    }

    private static byte[] Adts(int frames)
    {
      var bytes = new byte[frames * 16];
      for (var frame = 0; frame < frames; frame++) { bytes[frame * 16] = 0xFF; bytes[frame * 16 + 1] = 0xF1; bytes[frame * 16 + 4] = 2; bytes[frame * 16 + 5] = 0x1F; }
      return bytes;
    }

    private static byte[] Mp4()
    {
      static byte[] Box(string type, params byte[][] parts) { var payload = parts.SelectMany(part => part).ToArray(); return [.. Size(payload.Length + 8), .. System.Text.Encoding.ASCII.GetBytes(type), .. payload]; }
      static byte[] Size(int size) => [(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size];
      var header = new byte[] { 0, 0, 0, 0 };
      var times = new byte[8];
      var mvhd = Box("mvhd", header, times, Size(1000), new byte[84]);
      var mdhd = Box("mdhd", header, times, Size(44_100), new byte[8]);
      var elst = Box("elst", header, Size(1), Size(1), Size(0), new byte[4]);
      return [.. Box("moov", mvhd, Box("trak", Box("edts", elst), Box("mdia", mdhd)))];
    }
  }

  private sealed class MetadataWriter : IMp4MetadataWriter
  {
    public void Write(string path, IReadOnlyDictionary<string, string> tags) => File.WriteAllText(path, "generated output");
  }

  private sealed class CallbackProgress(Action<ConversionProgress> report) : IProgress<ConversionProgress>
  {
    public void Report(ConversionProgress value) => report(value);
  }
}
