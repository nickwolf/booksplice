using System.Security.Cryptography;

namespace BookSplice.FFmpeg.Validation;

public interface ICoverPayloadValidator
{
  IReadOnlyList<string> GetPayloadHashes(string outputPath);
}

public sealed class CoverPayloadValidator : ICoverPayloadValidator
{
  public IReadOnlyList<string> GetPayloadHashes(string outputPath)
  {
    using var file = TagLib.File.Create(outputPath);
    return file.Tag.Pictures.Select(picture => Convert.ToHexString(SHA256.HashData(picture.Data.Data)).ToLowerInvariant()).ToArray();
  }
}
