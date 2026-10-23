using BookSplice.Core.Chapters;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Planning;
using BookSplice.FFmpeg.Commands;

namespace BookSplice.FFmpeg.Execution;

public interface IConversionWorkspaceFactory
{
  string CreateJobDirectory();
}

public sealed class FileConversionWorkspaceFactory : IConversionWorkspaceFactory
{
  private readonly string _root;
  private readonly Func<string, FileAttributes> _getAttributes;

  public FileConversionWorkspaceFactory(string temporaryRoot)
    : this(temporaryRoot, GetExistingAttributes) { }

  internal FileConversionWorkspaceFactory(string temporaryRoot, Func<string, FileAttributes> getAttributes)
  {
    ArgumentNullException.ThrowIfNull(getAttributes);
    _root = NormalizeRoot(temporaryRoot);
    _getAttributes = getAttributes;
  }

  public string CreateJobDirectory()
  {
    var safetyRoot = Path.GetPathRoot(_root) ?? _root;
    if (HasReparsePointInPath(safetyRoot, _root, _getAttributes)) throw new IOException("The temporary root contains a reparse point.");
    Directory.CreateDirectory(_root);
    if (HasReparsePointInPath(safetyRoot, _root, _getAttributes)) throw new IOException("The temporary root contains a reparse point.");
    var job = Path.GetFullPath(Path.Combine(_root, Guid.NewGuid().ToString("N")));
    if (!IsContained(_root, job)) throw new IOException("The temporary job directory is outside the temporary root.");
    Directory.CreateDirectory(job);
    if ((_getAttributes(job) & FileAttributes.ReparsePoint) != 0) throw new IOException("The temporary job directory is a reparse point.");
    return job;
  }

  internal static string NormalizeRoot(string path)
  {
    ValidatePath(path);
    if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Temporary root must be fully qualified.", nameof(path));
    var root = Path.GetFullPath(path);
    var driveRoot = Path.GetPathRoot(root);
    return string.Equals(root, driveRoot, StringComparison.OrdinalIgnoreCase) ? root : root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
  }

  internal static bool IsContained(string root, string path)
  {
    var relative = Path.GetRelativePath(root, path);
    return !Path.IsPathRooted(relative) && relative is not "." && !relative.Equals("..", StringComparison.Ordinal) && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
  }

  internal static void ValidatePath(string path)
  {
    if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new ArgumentException("The path is unsafe.", nameof(path));
  }

  internal static bool HasReparsePointInPath(string root, string path, Func<string, FileAttributes>? getAttributes = null)
  {
    var current = Path.GetFullPath(path);
    var normalizedRoot = Path.GetFullPath(root);
    while (true)
    {
      var attributes = getAttributes is null ? GetExistingAttributes(current) : getAttributes(current);
      if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
      if (string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase)) return false;
      var parent = Directory.GetParent(current)?.FullName;
      if (parent is null || !IsContained(normalizedRoot, parent) && !string.Equals(parent, normalizedRoot, StringComparison.OrdinalIgnoreCase)) return false;
      current = parent;
    }
  }

  private static FileAttributes GetExistingAttributes(string path)
  {
    try { return File.GetAttributes(path); }
    catch (FileNotFoundException) { return 0; }
    catch (DirectoryNotFoundException) { return 0; }
  }
}

public sealed class FFmpegConversionExecutor : IConversionExecutor
{
  private readonly IProcessRunner _runner;
  private readonly FFmpegCommandFactory _commands;
  private readonly IMp4MetadataWriter _metadataWriter;
  private readonly IConversionWorkspaceFactory _workspaceFactory;
  private readonly TimeProvider _timeProvider;

  public FFmpegConversionExecutor(IProcessRunner runner, FFmpegCommandFactory commands, IMp4MetadataWriter metadataWriter, string temporaryRoot, TimeProvider? timeProvider = null)
    : this(runner, commands, metadataWriter, new FileConversionWorkspaceFactory(temporaryRoot), timeProvider) { }

  public FFmpegConversionExecutor(IProcessRunner runner, FFmpegCommandFactory commands, IMp4MetadataWriter metadataWriter, IConversionWorkspaceFactory workspaceFactory, TimeProvider? timeProvider = null)
  {
    _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    _commands = commands ?? throw new ArgumentNullException(nameof(commands));
    _metadataWriter = metadataWriter ?? throw new ArgumentNullException(nameof(metadataWriter));
    _workspaceFactory = workspaceFactory ?? throw new ArgumentNullException(nameof(workspaceFactory));
    _timeProvider = timeProvider ?? TimeProvider.System;
  }

  public async Task<ConversionExecutionResult> ExecuteAsync(ConversionPlan plan, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
  {
    ArgumentNullException.ThrowIfNull(plan);
    var processes = new List<ProcessResult>();
    var artifacts = new List<string>();
    string? job = null;
    string? output = null;
    var normalizer = new ConversionProgressNormalizer(plan, progress, _timeProvider);
    try
    {
      if (cancellationToken.IsCancellationRequested) return Cancelled(processes);
      IReadOnlyDictionary<string, string> tags;
      string metadata;
      string? manifest = null;
      string? filter = null;
      string[]? segments = null;
      using (normalizer.BeginPhase(ConversionPhase.Preparation))
      {
        ValidatePlanPaths(plan);
        job = Path.GetFullPath(_workspaceFactory.CreateJobDirectory());
        if ((File.GetAttributes(job) & FileAttributes.ReparsePoint) != 0) throw new IOException("The temporary job directory is a reparse point.");
        output = Register(artifacts, job, "output.m4b");
        tags = plan.MetadataProfile.Apply(plan.Metadata);
        metadata = Register(artifacts, job, "metadata.ffmeta");
        WriteMetadata(metadata, tags, plan.Chapters);
        if (plan.Strategy == AudioStrategy.SegmentedTranscode || plan.Strategy == AudioStrategy.AacStreamCopy && plan.SourcePaths.Count > 1)
        {
          manifest = Register(artifacts, job, "concat.txt");
          ConcatManifestWriter.WriteFile(manifest, plan.SourcePaths);
        }
        if (plan.Strategy == AudioStrategy.FilterConcatTranscode)
        {
          filter = Register(artifacts, job, "filter.txt");
          FilterGraphWriter.WriteFile(filter, plan.SourcePaths.Count, plan.SampleRate, plan.Channels);
        }
        if (plan.Strategy == AudioStrategy.SegmentedTranscode) segments = plan.SourcePaths.Select((_, index) => Register(artifacts, job, $"segment-{index:D4}.m4a")).ToArray();
      }
      if (plan.Strategy == AudioStrategy.SegmentedTranscode)
      {
        using (normalizer.BeginPhase(ConversionPhase.Encoding))
        {
          var segmentResults = await RunSegmentsAsync(plan, segments!, normalizer, cancellationToken).ConfigureAwait(false);
          processes.AddRange(segmentResults);
        }
        using (normalizer.BeginPhase(ConversionPhase.Assembly))
        {
          ConcatManifestWriter.WriteFile(manifest!, segments!);
          var finalAssembly = await RunStageAsync(_commands.CreateFinal(plan, output, manifest, filter, metadata), normalizer, plan.SourcePaths.Count, null, cancellationToken).ConfigureAwait(false);
          processes.Add(finalAssembly);
          if (finalAssembly.ExitCode != 0) throw new FFmpegProcessFailedException(finalAssembly.ExitCode);
        }
      }
      else
      {
        var phase = plan.Strategy == AudioStrategy.AacStreamCopy ? ConversionPhase.StreamCopy : ConversionPhase.Encoding;
        using (normalizer.BeginPhase(phase))
        {
          var final = await RunStageAsync(_commands.CreateFinal(plan, output, manifest, filter, metadata), normalizer, plan.SourcePaths.Count, null, cancellationToken).ConfigureAwait(false);
          processes.Add(final);
          if (final.ExitCode != 0) throw new FFmpegProcessFailedException(final.ExitCode);
        }
      }
      using (normalizer.BeginPhase(ConversionPhase.MetadataWriting))
      {
        try { _metadataWriter.Write(output, tags); }
        catch (Exception exception) when (exception is not OperationCanceledException) { throw new MetadataWriteException(exception); }
        normalizer.CompleteExecution();
      }
      return new ConversionExecutionResult(ExecutionStatus.Succeeded, output, Copy(processes), []);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { CleanupFailure(normalizer, artifacts, job); return Cancelled(processes); }
    catch (FFmpegProcessFailedException exception) { CleanupFailure(normalizer, artifacts, job); return Failed(processes, "execution.process-failed", $"FFmpeg exited with code {exception.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}."); }
    catch (MetadataWriteException) { CleanupFailure(normalizer, artifacts, job); return Failed(processes, "execution.metadata-write-failed", "The converted file metadata could not be written."); }
    catch (ProcessExecutionException) { CleanupFailure(normalizer, artifacts, job); return Failed(processes, "execution.process-start-failed", "FFmpeg could not be started."); }
    catch (ArgumentException) { CleanupFailure(normalizer, artifacts, job); return Failed(processes, "execution.invalid-path", "A conversion path is invalid."); }
    catch (UnauthorizedAccessException) { CleanupFailure(normalizer, artifacts, job); return Failed(processes, "execution.file-write-failed", "A conversion workspace file could not be written."); }
    catch (IOException) { CleanupFailure(normalizer, artifacts, job); return Failed(processes, "execution.file-write-failed", "A conversion workspace file could not be written."); }
    catch (InvalidOperationException) { CleanupFailure(normalizer, artifacts, job); return Failed(processes, "execution.process-start-failed", "FFmpeg could not be started."); }
    catch (Exception) { CleanupFailure(normalizer, artifacts, job); return Failed(processes, "execution.failed", "The conversion could not be completed."); }
  }

  private static void CleanupFailure(ConversionProgressNormalizer normalizer, IEnumerable<string> artifacts, string? job)
  {
    using (normalizer.BeginPhase(ConversionPhase.Cleanup)) Cleanup(artifacts, job);
  }

  private async Task<ProcessResult[]> RunSegmentsAsync(ConversionPlan plan, string[] segments, ConversionProgressNormalizer normalizer, CancellationToken cancellationToken)
  {
    var results = new ProcessResult[plan.SourcePaths.Count];
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    using var gate = new SemaphoreSlim(Math.Max(1, Math.Min(plan.ConversionJobs, plan.SourcePaths.Count)));
    var tasks = plan.SourcePaths.Select((source, index) => RunSegmentAsync(source, index, segments[index], plan, results, gate, normalizer, linked.Token)).ToArray();
    try { await Task.WhenAll(tasks).ConfigureAwait(false); }
    catch
    {
      linked.Cancel();
      try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
      var processFailure = tasks.Select(task => task.Exception?.GetBaseException()).OfType<FFmpegProcessFailedException>().FirstOrDefault();
      if (processFailure is not null) throw processFailure;
      throw;
    }
    return results;
  }

  private async Task RunSegmentAsync(string source, int index, string output, ConversionPlan plan, ProcessResult[] results, SemaphoreSlim gate, ConversionProgressNormalizer normalizer, CancellationToken cancellationToken)
  {
    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      var result = await RunStageAsync(_commands.CreateSegment(plan, source, output), normalizer, index, index, cancellationToken).ConfigureAwait(false);
      if (result.ExitCode != 0) throw new FFmpegProcessFailedException(result.ExitCode);
      results[index] = result;
    }
    finally { gate.Release(); }
  }

  private async Task<ProcessResult> RunStageAsync(ProcessSpec spec, ConversionProgressNormalizer normalizer, int stageIndex, int? sourceIndex, CancellationToken cancellationToken)
  {
    var outputProgress = normalizer.ForProcess(stageIndex, sourceIndex);
    try
    {
      var result = await _runner.RunAsync(spec, outputProgress, cancellationToken).ConfigureAwait(false);
      if (result.ExitCode == 0) normalizer.CompleteProcess(stageIndex, sourceIndex);
      else normalizer.RetireProcess(stageIndex, sourceIndex);
      return result with { Executable = spec.FileName, Arguments = Array.AsReadOnly(spec.Arguments.ToArray()) };
    }
    catch
    {
      normalizer.RetireProcess(stageIndex, sourceIndex);
      throw;
    }
  }

  private static string Register(List<string> artifacts, string job, string name)
  {
    FileConversionWorkspaceFactory.ValidatePath(name);
    var path = Path.GetFullPath(Path.Combine(job, name));
    if (!FileConversionWorkspaceFactory.IsContained(job, path)) throw new IOException("The artifact is outside the conversion workspace.");
    artifacts.Add(path);
    return path;
  }

  private static void ValidatePlanPaths(ConversionPlan plan)
  {
    FileConversionWorkspaceFactory.ValidatePath(plan.OutputPath);
    _ = Path.GetFullPath(plan.OutputPath);
    foreach (var source in plan.SourcePaths) { FileConversionWorkspaceFactory.ValidatePath(source); _ = Path.GetFullPath(source); }
    if (plan.Cover is { } cover) { FileConversionWorkspaceFactory.ValidatePath(cover.SourcePath); _ = Path.GetFullPath(cover.SourcePath); }
  }

  private static void WriteMetadata(string path, IReadOnlyDictionary<string, string> tags, IReadOnlyList<ChapterEntry> chapters)
  {
    var value = new FFmetadataWriter().Write(tags);
    foreach (var chapter in chapters)
      value += $"[CHAPTER]\nTIMEBASE=1/1000000\nSTART={chapter.StartMicroseconds}\nEND={chapter.EndMicroseconds}\ntitle={Escape(chapter.Title)}\n";
    File.WriteAllText(path, value, new System.Text.UTF8Encoding(false));
  }

  private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\r\n", "\\\n").Replace("\n", "\\\n").Replace("\r", "\\\n");

  internal static void Cleanup(IEnumerable<string> artifacts, string? job, Func<string, FileAttributes>? getAttributes = null)
  {
    foreach (var path in artifacts.Distinct(StringComparer.OrdinalIgnoreCase))
    {
      try
      {
        if (job is null) continue;
        var normalizedJob = Path.GetFullPath(job);
        var normalizedPath = Path.GetFullPath(path);
        if (!FileConversionWorkspaceFactory.IsContained(normalizedJob, normalizedPath)) continue;
        var safetyRoot = Path.GetPathRoot(normalizedJob) ?? normalizedJob;
        if (FileConversionWorkspaceFactory.HasReparsePointInPath(safetyRoot, normalizedPath, getAttributes)) continue;
        var attributes = getAttributes is null ? File.GetAttributes(normalizedPath) : getAttributes(normalizedPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || !File.Exists(normalizedPath)) continue;
        if (!FileConversionWorkspaceFactory.IsContained(normalizedJob, Path.GetFullPath(normalizedPath))) continue;
        File.Delete(normalizedPath);
      }
      catch { }
    }
    try
    {
      if (job is null) return;
      var normalizedJob = Path.GetFullPath(job);
      var jobAttributes = getAttributes is null ? File.GetAttributes(normalizedJob) : getAttributes(normalizedJob);
      if ((jobAttributes & FileAttributes.ReparsePoint) != 0 || !Directory.Exists(normalizedJob)) return;
      var safetyRoot = Path.GetPathRoot(normalizedJob) ?? normalizedJob;
      if (FileConversionWorkspaceFactory.HasReparsePointInPath(safetyRoot, normalizedJob, getAttributes)) return;
      if (!Directory.EnumerateFileSystemEntries(normalizedJob).Any()) Directory.Delete(normalizedJob);
    }
    catch { }
  }

  private static ConversionExecutionResult Cancelled(IReadOnlyList<ProcessResult> processes) => new(ExecutionStatus.Cancelled, null, Copy(processes), [new("execution.cancelled", "The conversion was cancelled.")]);
  private static ConversionExecutionResult Failed(IReadOnlyList<ProcessResult> processes, string code, string message) => new(ExecutionStatus.Failed, null, Copy(processes), [new(code, message)]);
  private static System.Collections.ObjectModel.ReadOnlyCollection<ExecutionProcessResult> Copy(IEnumerable<ProcessResult> processes) => Array.AsReadOnly(processes.Select(process => new ExecutionProcessResult(process.ExitCode, process.StandardOutput, process.StandardError, process.ChildCpuTime, process.Executable, process.Arguments)).ToArray());
  private sealed class FFmpegProcessFailedException(int exitCode) : Exception { public int ExitCode { get; } = exitCode; }
  private sealed class MetadataWriteException(Exception inner) : Exception("Metadata write failed.", inner);
}
