namespace AudiobookConverter.FFmpeg.Execution;

public sealed record ProcessSpec(
  string FileName,
  IReadOnlyList<string> Arguments,
  string? WorkingDirectory = null,
  TimeSpan? GracefulCancellation = null);
