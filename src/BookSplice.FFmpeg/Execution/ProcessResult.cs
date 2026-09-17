namespace BookSplice.FFmpeg.Execution;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan? ChildCpuTime = null)
{
  public string? Executable { get; init; }
  public IReadOnlyList<string>? Arguments { get; init; }
}
