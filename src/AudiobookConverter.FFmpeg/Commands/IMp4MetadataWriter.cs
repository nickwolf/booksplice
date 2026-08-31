namespace AudiobookConverter.FFmpeg.Commands;

public interface IMp4MetadataWriter
{
  void Write(string path, IReadOnlyDictionary<string, string> tags);
}
