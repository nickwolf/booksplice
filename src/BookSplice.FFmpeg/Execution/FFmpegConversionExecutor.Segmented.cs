using BookSplice.Core.Planning;
using BookSplice.FFmpeg.Commands;

namespace BookSplice.FFmpeg.Execution;

public sealed partial class FFmpegConversionExecutor
{
  private async Task<IReadOnlyList<SegmentRange>> MeasureAndPlanRangesAsync(ConversionPlan plan, List<ProcessResult> processes, CancellationToken cancellationToken)
  {
    var measured = await RunLimitedAsync(plan.SourcePaths.Count, plan.ConversionJobs, async (index, token) =>
    {
      var spec = _commands.CreateMeasure(plan, plan.SourcePaths[index]);
      var result = await _runner.RunAsync(spec, null, token).ConfigureAwait(false);
      if (result.ExitCode != 0)
      {
        lock (processes) processes.Add(result with { Executable = spec.FileName, Arguments = Array.AsReadOnly(spec.Arguments.ToArray()) });
        throw new FFmpegProcessFailedException(result.ExitCode);
      }
      var samples = SourceSampleCounter.Parse(result.StandardError, out var summary);
      return (Samples: samples, Result: result with { StandardError = summary, Executable = spec.FileName, Arguments = Array.AsReadOnly(spec.Arguments.ToArray()) });
    }, cancellationToken).ConfigureAwait(false);
    processes.AddRange(measured.Select(item => item.Result));
    return SegmentRangePlanner.Plan(measured.Select(item => item.Samples).ToArray(), plan.ConversionJobs);
  }

  private async Task<ProcessResult[]> EncodeRangesAsync(ConversionPlan plan, IReadOnlyList<SegmentRange> ranges, IReadOnlyList<string> segments, ConversionProgressNormalizer normalizer, CancellationToken cancellationToken)
    => await RunLimitedAsync(ranges.Count, plan.ConversionJobs, async (index, token) =>
    {
      var range = ranges[index];
      var spec = _commands.CreateRange(plan, range.Inputs.Select(input => plan.SourcePaths[input]).ToArray(), range.TrimStart, range.TrimEnd, segments[index]);
      var result = await RunStageAsync(spec, normalizer, index, index, token).ConfigureAwait(false);
      if (result.ExitCode != 0) throw new FFmpegProcessFailedException(result.ExitCode);
      return result;
    }, cancellationToken).ConfigureAwait(false);

  // Keeps each range's own frames, then wraps the stream in an MP4 whose edit list hides the encoder's first frame and the padding after the last sample.
  private async Task JoinRangesAsync(ConversionPlan plan, IReadOnlyList<SegmentRange> ranges, IReadOnlyList<string> segments, string joined, string intermediate, List<ProcessResult> processes, CancellationToken cancellationToken)
  {
    long total = 0;
    using (var output = new FileStream(joined, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
    {
      foreach (var range in ranges)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var lengths = AdtsFrames.ReadLengths(segments[range.Index]);
        if (lengths.Count != range.ExpectedFrames) throw new SegmentedAssemblyException("A segment has an unexpected number of frames.");
        AdtsFrames.Append(segments[range.Index], lengths, range.FirstKeptFrame, range.LastKeptFrame, output);
        File.Delete(segments[range.Index]);
        total = range.End;
      }
    }
    var spec = _commands.CreateIntermediate(plan, joined, intermediate);
    var result = await _runner.RunAsync(spec, null, cancellationToken).ConfigureAwait(false);
    processes.Add(result with { Executable = spec.FileName, Arguments = Array.AsReadOnly(spec.Arguments.ToArray()) });
    if (result.ExitCode != 0) throw new FFmpegProcessFailedException(result.ExitCode);
    File.Delete(joined);
    try { Mp4MetadataBoxes.PatchSingleEditList(intermediate, SegmentRangePlanner.FrameSamples, total, plan.SampleRate); }
    catch (InvalidDataException exception) { throw new SegmentedAssemblyException(exception.Message); }
  }

  private static async Task<T[]> RunLimitedAsync<T>(int count, int jobs, Func<int, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
  {
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    using var gate = new SemaphoreSlim(Math.Max(1, Math.Min(jobs, count)));
    var tasks = Enumerable.Range(0, count).Select(async index =>
    {
      await gate.WaitAsync(linked.Token).ConfigureAwait(false);
      try { return await work(index, linked.Token).ConfigureAwait(false); }
      catch { linked.Cancel(); throw; }
      finally { gate.Release(); }
    }).ToArray();
    try { return await Task.WhenAll(tasks).ConfigureAwait(false); }
    catch
    {
      linked.Cancel();
      try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
      var failure = tasks.Select(task => task.Exception?.GetBaseException()).FirstOrDefault(exception => exception is FFmpegProcessFailedException or SegmentedAssemblyException);
      if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
      throw;
    }
  }
}
