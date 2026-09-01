using AudiobookConverter.Core.Metadata;
using AudiobookConverter.Core.Planning;
using AudiobookConverter.FFmpeg.Commands;

namespace AudiobookConverter.FFmpeg.Execution;

public enum ExecutionStatus { Succeeded, Failed, Cancelled }
public sealed record ExecutionDiagnostic(string Code, string Message);
public sealed record ConversionExecutionResult(ExecutionStatus Status, string? TemporaryOutputPath, IReadOnlyList<ProcessResult> Processes, IReadOnlyList<ExecutionDiagnostic> Diagnostics);
public interface IConversionExecutor { Task<ConversionExecutionResult> ExecuteAsync(ConversionPlan plan, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null); }
public sealed class FFmpegConversionExecutor(IProcessRunner runner, FFmpegCommandFactory commands, IMp4MetadataWriter metadataWriter, string temporaryRoot) : IConversionExecutor
{
  public async Task<ConversionExecutionResult> ExecuteAsync(ConversionPlan plan, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
  {
    var job = Path.Combine(Path.GetFullPath(temporaryRoot), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
    var output = Path.Combine(job, "output.m4b"); var artifacts = new List<string> { output }; var processes = new List<ProcessResult>();
    try
    {
      cancellationToken.ThrowIfCancellationRequested();
      string? manifest = null;
      if (plan.Strategy is AudioStrategy.AacStreamCopy or AudioStrategy.SegmentedTranscode) { manifest = Path.Combine(job, "concat.txt"); ConcatManifestWriter.WriteFile(manifest, plan.SourcePaths); artifacts.Add(manifest); }
      if (plan.Strategy == AudioStrategy.SegmentedTranscode)
      {
        var segments = plan.SourcePaths.Select((_, i) => Path.Combine(job, $"segment-{i:D4}.aac")).ToArray(); artifacts.AddRange(segments);
        foreach (var pair in plan.SourcePaths.Zip(segments)) { var result = await runner.RunAsync(commands.CreateSegment(plan, pair.First, pair.Second), null, cancellationToken); processes.Add(result); if (result.ExitCode != 0) throw new InvalidOperationException("FFmpeg conversion failed."); }
        ConcatManifestWriter.WriteFile(manifest!, segments);
      }
      var final = await runner.RunAsync(commands.CreateFinal(plan, output, manifest), null, cancellationToken); processes.Add(final); if (final.ExitCode != 0) throw new InvalidOperationException("FFmpeg conversion failed.");
      var tags = ResolveProfile(plan.MetadataProfileId).Apply(plan.Metadata); metadataWriter.Write(output, tags);
      return new ConversionExecutionResult(ExecutionStatus.Succeeded, output, processes, []);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Cleanup(artifacts, job); return new ConversionExecutionResult(ExecutionStatus.Cancelled, null, processes, [new("execution.cancelled", "The conversion was cancelled.")]); }
    catch (Exception) { Cleanup(artifacts, job); return new ConversionExecutionResult(ExecutionStatus.Failed, null, processes, [new("execution.failed", "The conversion could not be completed.")]); }
  }
  private static MetadataProfile ResolveProfile(string id) => id == "GenericMp4" ? MetadataProfiles.GenericMp4 : id == "NickMp3tag" ? MetadataProfiles.NickMp3tag : throw new InvalidOperationException();
  private static void Cleanup(IEnumerable<string> artifacts, string job) { foreach (var path in artifacts) if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path); if (Directory.Exists(job) && !Directory.EnumerateFileSystemEntries(job).Any()) Directory.Delete(job); }
}
