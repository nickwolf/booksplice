using System.Globalization;
using BookSplice.Core.Preview;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.FFmpeg.Preview;

/// <summary>Decodes a bounded preview region to a temporary WAV. Sources are only read.</summary>
public sealed class PreviewDecoder(IProcessRunner runner, MediaToolSet tools) : IPreviewDecoder
{
  public async Task DecodeAsync(PreviewRegion region, string outputWavPath, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(region);
    if (region.Pieces.Count == 0) throw new ArgumentException("The preview region has no audio.", nameof(region));
    var partial = outputWavPath + ".partial";
    var pieceFiles = new List<string>();
    try
    {
      for (var index = 0; index < region.Pieces.Count; index++)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var piece = region.Pieces[index];
        var pieceFile = $"{outputWavPath}.piece{index}";
        pieceFiles.Add(pieceFile);
        string[] arguments =
        [
          "-hide_banner", "-nostdin", "-v", "error",
          "-ss", Seconds(piece.OffsetMicroseconds), "-t", Seconds(piece.LengthMicroseconds),
          "-i", piece.Path, "-map", "0:a:0", "-vn", "-ac", "2", "-ar", "44100", "-c:a", "pcm_s16le", "-f", "wav", "-y", pieceFile
        ];
        var result = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath, arguments), null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(pieceFile)) throw new PreviewUnavailableException(result.StandardError);
      }
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        WavJoiner.Join(pieceFiles, partial);
      }
      catch (InvalidDataException exception)
      {
        throw new PreviewUnavailableException(exception.Message, exception);
      }
      File.Move(partial, outputWavPath, true);
    }
    finally
    {
      foreach (var file in pieceFiles) TryDelete(file);
      TryDelete(partial);
    }
  }

  private static string Seconds(long microseconds) => (microseconds / 1_000_000m).ToString("F6", CultureInfo.InvariantCulture);

  private static void TryDelete(string path)
  {
    try { File.Delete(path); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
  }
}
