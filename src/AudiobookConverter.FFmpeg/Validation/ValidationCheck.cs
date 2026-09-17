namespace AudiobookConverter.FFmpeg.Validation;

public static class ValidationCodes
{
  public const string FileExists = "file.exists";
  public const string ContainerParse = "container.parse";
  public const string AudioPrimaryCount = "audio.primary-count";
  public const string AudioCodec = "audio.codec";
  public const string AudioSampleRate = "audio.sample-rate";
  public const string AudioLayout = "audio.layout";
  public const string AudioExtra = "audio.extra";
  public const string DurationExpected = "duration.expected";
  public const string PacketsEndRegion = "packets.end-region";
  public const string ChaptersCount = "chapters.count";
  public const string ChaptersMonotonic = "chapters.monotonic";
  public const string ChaptersTiming = "chapters.timing";
  public const string CoverPresence = "cover.presence";
  public const string CoverPayload = "cover.payload";
  public const string MetadataRequired = "metadata.required";
  public const string FullDecode = "decode.full";
}
