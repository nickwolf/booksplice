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
  public async Task SegmentedJobWithoutChaptersWaitsForExecutionCompletionToShowFinalizing()
  {
    var root = Directory.CreateTempSubdirectory("booksplice-progress-").FullName;
    try
    {
      var sources = Enumerable.Range(0, 33).Select(index => Path.Combine(root, $"source-{index:D2}.mp3")).ToArray();
      var metadata = new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(),
        new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
      var plan = new ConversionPlan(sources, metadata, null, [], QualityProfileCatalog.Version1[2],
        ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, Path.Combine(root, "finished.m4b"),
        AudioStrategy.SegmentedTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 1, "test", MetadataProfiles.GenericMp4);
      var executor = new FFmpegConversionExecutor(new ReportingRunner(),
        new FFmpegCommandFactory(new MediaToolSet("ffmpeg.exe", "ffprobe.exe", "pinned", "pinned")),
        new MetadataWriter(), Path.Combine(root, "jobs"));
      var display = new ConversionProgressDisplay();
      string? whileLastSegmentEncodes = null;
      double? whileLastSegmentPercent = null;
      var result = await executor.ExecuteAsync(plan, CancellationToken.None, new CallbackProgress(value =>
      {
        display.Report(value, TimeSpan.FromSeconds(30));
        if (value.SourceIndex == 32 && value.OutTimeMicroseconds is > 0 && value.State is null)
        {
          whileLastSegmentEncodes = display.Describe(TimeSpan.FromSeconds(30));
          whileLastSegmentPercent = value.DisplayPercent;
        }
      }));

      Assert.Equal(ExecutionStatus.Succeeded, result.Status);
      Assert.Equal(99, whileLastSegmentPercent);
      Assert.NotNull(whileLastSegmentEncodes);
      Assert.DoesNotContain("Finalizing", whileLastSegmentEncodes, StringComparison.Ordinal);
      Assert.Contains("Speed:", whileLastSegmentEncodes, StringComparison.Ordinal);
      Assert.Equal("Elapsed: 30s · Finalizing…", display.Describe(TimeSpan.FromSeconds(30)));
    }
    finally { Directory.Delete(root, true); }
  }

  private sealed class ReportingRunner : IProcessRunner
  {
    public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      progress?.Report("out_time_us=5000000");
      progress?.Report("speed=12.3x");
      progress?.Report("progress=continue");
      return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
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
