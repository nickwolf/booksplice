namespace AudiobookConverter.FFmpeg.Tools;

public sealed record MediaToolSet(
  string FFmpegPath,
  string FFprobePath,
  string FFmpegVersion,
  string FFprobeVersion);
