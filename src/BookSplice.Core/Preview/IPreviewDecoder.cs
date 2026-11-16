namespace BookSplice.Core.Preview;

/// <summary>Decodes a bounded preview region to a WAV file.</summary>
public interface IPreviewDecoder
{
  Task DecodeAsync(PreviewRegion region, string outputWavPath, CancellationToken cancellationToken);
}
