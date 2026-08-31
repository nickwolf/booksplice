namespace AudiobookConverter.FFmpeg.Execution;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
