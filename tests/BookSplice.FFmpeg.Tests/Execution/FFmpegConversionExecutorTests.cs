using BookSplice.Core.Chapters;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.FFmpeg.Commands;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.FFmpeg.Tests.Execution;

public sealed class FFmpegConversionExecutorTests
{
  [Fact]
  public async Task SegmentsRunWithinPlanBoundAndFinalArgumentsPreserveOrder()
  {
    using var root = new TemporaryDirectory();
    var runner = new FakeRunner { Delay = TimeSpan.FromMilliseconds(20) };
    var plan = Plan(AudioStrategy.SegmentedTranscode, 5, jobs: 2);
    var progress = new List<ConversionProgress>();
    var result = await Executor(runner, root.Path).ExecuteAsync(plan, CancellationToken.None, new InlineProgress<ConversionProgress>(progress.Add));

    Assert.Equal(ExecutionStatus.Succeeded, result.Status);
    Assert.Equal(2, runner.MaximumActive);
    var segmentSources = runner.Specs.Take(5).Select(spec => (Source: spec.Arguments[spec.Arguments.ToList().IndexOf("-i") + 1], Output: spec.Arguments[^1])).ToArray();
    Assert.Equal(plan.SourcePaths.OrderBy(path => path), segmentSources.OrderBy(pair => pair.Source).Select(pair => pair.Source));
    var final = runner.Specs[^1];
    Assert.Equal("copy", final.Arguments[final.Arguments.ToList().IndexOf("-c:a") + 1]);
    Assert.DoesNotContain(final.Arguments, value => value == "aac");
    var manifest = final.Arguments[final.Arguments.ToList().IndexOf("-i") + 1];
    Assert.Equal(Enumerable.Range(0, 5).Select(index => $"file '" + Path.Combine(Path.GetDirectoryName(manifest)!, $"segment-{index:D4}.aac").Replace('\\', '/') + "'"), File.ReadAllLines(manifest));
    Assert.True(progress.Zip(progress.Skip(1), (left, right) => (left.DisplayPercent ?? 0) <= (right.DisplayPercent ?? 0)).All(value => value));
    Assert.Equal(100, progress[^1].DisplayPercent);
    Assert.DoesNotContain(plan.OutputPath, runner.Specs.SelectMany(spec => spec.Arguments));
  }

  [Fact]
  public async Task OneSourceStreamCopyRunsWithoutCreatingAConcatManifest()
  {
    using var root = new TemporaryDirectory();
    var runner = new FakeRunner();
    var result = await Executor(runner, root.Path).ExecuteAsync(Plan(AudioStrategy.AacStreamCopy, 1, 1), CancellationToken.None);

    Assert.Equal(ExecutionStatus.Succeeded, result.Status);
    Assert.Single(runner.Specs);
    Assert.DoesNotContain(runner.Specs[0].Arguments, value => value.Contains("concat", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(runner.Specs[0].Arguments, value => value.EndsWith(@"source-00.mp3", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public void RelativeTemporaryRootIsRejectedBeforeNormalization()
  {
    Assert.Throws<ArgumentException>(() => new FFmpegConversionExecutor(new FakeRunner(), new FFmpegCommandFactory(new MediaToolSet("ffmpeg.exe", "ffprobe.exe", "pinned", "pinned")), new FakeMetadataWriter(), "relative-root"));
  }

  [Fact]
  public void TemporaryRootCreationRejectsReparseParentBeforeTraversal()
  {
    using var root = new TemporaryDirectory();
    var temporaryRoot = Path.Combine(root.Path, "jobs", "nested");
    var reparseParent = Path.GetDirectoryName(temporaryRoot)!;
    var target = Path.Combine(root.Path, "target.txt");
    var sibling = Path.Combine(root.Path, "sibling.txt");
    File.WriteAllText(target, "target");
    File.WriteAllText(sibling, "sibling");
    var attributes = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase)
    {
      [reparseParent] = FileAttributes.Directory | FileAttributes.ReparsePoint,
    };

    var workspace = new FileConversionWorkspaceFactory(temporaryRoot, path => attributes.TryGetValue(path, out var value) ? value : FileAttributes.Directory);

    Assert.Throws<IOException>(workspace.CreateJobDirectory);
    Assert.False(Directory.Exists(Path.Combine(root.Path, "jobs")));
    Assert.Equal("target", File.ReadAllText(target));
    Assert.Equal("sibling", File.ReadAllText(sibling));
  }

  [Fact]
  public void CleanupRefusesOutOfRootArtifactsAndReparseParents()
  {
    using var root = new TemporaryDirectory();
    var job = Directory.CreateDirectory(Path.Combine(root.Path, "job")).FullName;
    var registered = Path.Combine(job, "registered.txt");
    File.WriteAllText(registered, "registered");
    var source = Path.Combine(root.Path, "source.mp3");
    var destination = Path.Combine(root.Path, "destination.m4b");
    var sibling = Path.Combine(root.Path, "sibling.txt");
    var outside = Path.Combine(Path.GetDirectoryName(root.Path)!, $"outside-{Guid.NewGuid():N}.txt");
    File.WriteAllText(source, "source");
    File.WriteAllText(destination, "destination");
    File.WriteAllText(sibling, "sibling");
    File.WriteAllText(outside, "outside");
    var reparseParent = Path.GetDirectoryName(job)!;
    var attributes = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase) { [reparseParent] = FileAttributes.Directory | FileAttributes.ReparsePoint };

    FFmpegConversionExecutor.Cleanup([registered, outside], job, path => attributes.TryGetValue(path, out var value) ? value : File.GetAttributes(path));

    Assert.True(File.Exists(registered));
    Assert.True(File.Exists(source));
    Assert.True(File.Exists(destination));
    Assert.True(File.Exists(sibling));
    Assert.True(File.Exists(outside));
    File.Delete(outside);
  }

  [Fact]
  public async Task NonzeroExitReturnsStructuredFailureAndLeavesDestinationAndSiblingFiles()
  {
    using var root = new TemporaryDirectory();
    var sibling = Directory.CreateDirectory(Path.Combine(root.Path, "sibling-job"));
    var siblingFile = Path.Combine(sibling.FullName, "keep.txt");
    File.WriteAllText(siblingFile, "keep");
    var injected = Path.Combine(root.Path, "injected.txt");
    var runner = new FakeRunner { ExitCode = 7, OnRun = (_, _) => File.WriteAllText(injected, "keep") };
    var plan = Plan(AudioStrategy.DirectTranscode, 1, jobs: 1);
    var result = await Executor(runner, root.Path).ExecuteAsync(plan, CancellationToken.None);

    Assert.Equal(ExecutionStatus.Failed, result.Status);
    Assert.Equal("execution.process-failed", Assert.Single(result.Diagnostics).Code);
    Assert.Contains("7", result.Diagnostics[0].Message);
    Assert.True(File.Exists(injected));
    Assert.True(File.Exists(siblingFile));
    Assert.False(File.Exists(plan.OutputPath));
  }

  [Fact]
  public async Task MetadataFailureRemovesRegisteredOutputAndReturnsFailure()
  {
    using var root = new TemporaryDirectory();
    var writer = new FakeMetadataWriter { Exception = new InvalidOperationException() };
    var result = await Executor(new FakeRunner(), root.Path, writer).ExecuteAsync(Plan(AudioStrategy.DirectTranscode, 1, 1), CancellationToken.None);

    Assert.Equal(ExecutionStatus.Failed, result.Status);
    Assert.Equal("execution.metadata-write-failed", Assert.Single(result.Diagnostics).Code);
    Assert.False(result.TemporaryOutputPath is not null && File.Exists(result.TemporaryOutputPath));
  }

  [Fact]
  public async Task CancellationBeforeStartDoesNotCreateWorkspaceOrStartProcess()
  {
    using var root = new TemporaryDirectory();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var runner = new FakeRunner();
    var result = await Executor(runner, root.Path).ExecuteAsync(Plan(AudioStrategy.DirectTranscode, 1, 1), cancellation.Token);

    Assert.Equal(ExecutionStatus.Cancelled, result.Status);
    Assert.Empty(runner.Specs);
    Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
  }

  [Fact]
  public async Task CancellationInFlightCleansRegisteredFilesButPreservesUnregisteredFile()
  {
    using var root = new TemporaryDirectory();
    using var cancellation = new CancellationTokenSource();
    var injected = string.Empty;
    var runner = new FakeRunner { WaitForCancellation = true, OnRun = (spec, _) => { injected = Path.Combine(Path.GetDirectoryName(spec.Arguments[^1])!, "injected.txt"); File.WriteAllText(injected, "keep"); } };
    var task = Executor(runner, root.Path).ExecuteAsync(Plan(AudioStrategy.DirectTranscode, 1, 1), cancellation.Token);
    await runner.Started.Task;
    cancellation.Cancel();
    var result = await task;

    Assert.Equal(ExecutionStatus.Cancelled, result.Status);
    Assert.True(File.Exists(injected));
    Assert.Contains(Directory.EnumerateDirectories(root.Path), path => Directory.Exists(path));
  }

  [Fact]
  public async Task NickMp3tagProfileIsAppliedBeforeMetadataWriterCall()
  {
    using var root = new TemporaryDirectory();
    var fields = new Dictionary<SemanticField, AggregatedValue>
    {
      [SemanticField.BookTitle] = new(SemanticField.BookTitle, AggregationState.Consistent, "Book", []),
      [SemanticField.Author] = new(SemanticField.Author, AggregationState.Consistent, "Author", []),
      [SemanticField.Narrator] = new(SemanticField.Narrator, AggregationState.Consistent, "Narrator", []),
    };
    var metadata = new BookMetadata(fields, new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
    var writer = new FakeMetadataWriter();
    var result = await Executor(new FakeRunner(), root.Path, writer).ExecuteAsync(Plan(AudioStrategy.DirectTranscode, 1, 1, metadata: metadata, metadataProfileId: "NickMp3tag"), CancellationToken.None);

    Assert.Equal(ExecutionStatus.Succeeded, result.Status);
    Assert.Equal("Book", writer.LastTags!["TITLE"]);
    Assert.Equal("Author", writer.LastTags["ARTIST"]);
    Assert.Equal("Narrator", writer.LastTags["COMPOSER"]);
  }

  [Fact]
  public async Task ProcessStartFailureReturnsSanitizedStructuredFailure()
  {
    using var root = new TemporaryDirectory();
    var runner = new FakeRunner { ExceptionToThrow = new ProcessExecutionException("raw source path", new InvalidOperationException()) };
    var result = await Executor(runner, root.Path).ExecuteAsync(Plan(AudioStrategy.DirectTranscode, 1, 1), CancellationToken.None);

    Assert.Equal(ExecutionStatus.Failed, result.Status);
    Assert.Equal("execution.process-start-failed", Assert.Single(result.Diagnostics).Code);
    Assert.DoesNotContain("raw source path", result.Diagnostics[0].Message);
  }

  [Fact]
  public async Task JobFileWriteFailureReturnsStructuredFailureAndPreservesUnrelatedFiles()
  {
    using var root = new TemporaryDirectory();
    var sibling = Path.Combine(root.Path, "sibling.txt");
    File.WriteAllText(sibling, "keep");
    var destination = Path.Combine(root.Path, "destination.m4b");
    var source = Path.Combine(root.Path, "source.mp3");
    File.WriteAllText(source, "source");
    var jobFile = Path.Combine(root.Path, "job-file");
    var workspace = new FileJobWorkspaceFactory(jobFile);
    var result = await new FFmpegConversionExecutor(new FakeRunner(), new FFmpegCommandFactory(new MediaToolSet("ffmpeg.exe", "ffprobe.exe", "pinned", "pinned")), new FakeMetadataWriter(), workspace)
      .ExecuteAsync(Plan(AudioStrategy.DirectTranscode, 1, 1, destination, source), CancellationToken.None);

    Assert.Equal(ExecutionStatus.Failed, result.Status);
    Assert.Equal("execution.file-write-failed", Assert.Single(result.Diagnostics).Code);
    Assert.Equal("keep", File.ReadAllText(sibling));
    Assert.Equal("source", File.ReadAllText(source));
    Assert.False(File.Exists(destination));
    Assert.True(File.Exists(jobFile));
  }

  private static FFmpegConversionExecutor Executor(FakeRunner runner, string root, FakeMetadataWriter? writer = null) => new(runner, new FFmpegCommandFactory(new MediaToolSet("ffmpeg.exe", "ffprobe.exe", "pinned", "pinned")), writer ?? new FakeMetadataWriter(), root);

  private static ConversionPlan Plan(AudioStrategy strategy, int count, int jobs, string? destination = null, string? sourceOverride = null, BookMetadata? metadata = null, string metadataProfileId = "GenericMp4")
  {
    var sources = Enumerable.Range(0, count).Select(index => index == 0 && sourceOverride is not null ? sourceOverride : Path.Combine(Path.GetTempPath(), $"source-{index:D2}.mp3")).ToArray();
    var chapters = sources.Select((_, index) => new ChapterEntry(index * 10_000_000L, (index + 1) * 10_000_000L, $"Chapter {index + 1}", string.Empty, sources[index])).ToArray();
    metadata ??= new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>());
    return new ConversionPlan(sources, metadata, null, chapters, QualityProfileCatalog.Version1[2], ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, destination ?? Path.Combine(Path.GetTempPath(), $"planned-{Guid.NewGuid():N}.m4b"), strategy, [], new SpaceEstimate(1, 1, 1, 1, 0), jobs, "test", metadataProfileId);
  }

  private sealed class FakeRunner : IProcessRunner
  {
    private readonly object _sync = new();
    private int _active;
    public List<ProcessSpec> Specs { get; } = [];
    public int MaximumActive { get; private set; }
    public int ExitCode { get; init; }
    public Exception? ExceptionToThrow { get; init; }
    public TimeSpan Delay { get; init; }
    public bool WaitForCancellation { get; init; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action<ProcessSpec, IProgress<string>?>? OnRun { get; init; }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken)
    {
      lock (_sync) Specs.Add(spec);
      var active = Interlocked.Increment(ref _active);
      lock (_sync) MaximumActive = Math.Max(MaximumActive, active);
      Started.TrySetResult();
      OnRun?.Invoke(spec, progress);
      if (ExceptionToThrow is not null) throw ExceptionToThrow;
      progress?.Report("out_time_us=5000000");
      if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
      Interlocked.Decrement(ref _active);
      return new ProcessResult(ExitCode, string.Empty, string.Empty);
    }
  }

  private sealed class FakeMetadataWriter : IMp4MetadataWriter
  {
    public Exception? Exception { get; init; }
    public IReadOnlyDictionary<string, string>? LastTags { get; private set; }
    public void Write(string path, IReadOnlyDictionary<string, string> tags)
    {
      LastTags = tags;
      File.WriteAllText(path, "temporary output");
      if (Exception is not null) throw Exception;
    }
  }

  private sealed class FileJobWorkspaceFactory(string jobFile) : IConversionWorkspaceFactory
  {
    public string CreateJobDirectory()
    {
      File.WriteAllText(jobFile, "keep");
      return jobFile;
    }
  }

  private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory().FullName;
    public string Path { get; }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
  }
}
