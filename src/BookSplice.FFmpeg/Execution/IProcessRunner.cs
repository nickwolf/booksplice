namespace BookSplice.FFmpeg.Execution;

public interface IProcessRunner
{
  Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken);
}
