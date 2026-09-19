using System.Diagnostics;
using System.Globalization;
using BookSplice.Core.Covers;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.FFmpeg.Covers;

public sealed class MediaCoverPayloadOpener(MediaToolSet tools) : ICoverPayloadOpener
{
  public async ValueTask<Stream> OpenReadAsync(CoverPayloadReference payload, CancellationToken cancellationToken)
  {
    if (payload.Origin == CoverOrigin.ExternalFile)
      return await new FileSystemCoverPayloadOpener().OpenReadAsync(payload, cancellationToken);
    if (payload.EmbeddedPictureIndex is not >= 0) throw new IOException("The embedded cover has no stream index.");
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(30));
    var start = new ProcessStartInfo(tools.FFmpegPath)
    {
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true
    };
    foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", payload.SourcePath,
      "-map", $"0:{payload.EmbeddedPictureIndex.Value.ToString(CultureInfo.InvariantCulture)}", "-c:v", "copy",
      "-frames:v", "1", "-f", "image2pipe", "pipe:1" }) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException("Could not read the embedded cover.");
    using var registration = timeout.Token.Register(() =>
    {
      try { if (!process.HasExited) process.Kill(true); }
      catch (InvalidOperationException) { }
    });
    var errors = process.StandardError.ReadToEndAsync(timeout.Token);
    var output = new MemoryStream();
    try
    {
      var buffer = new byte[65536];
      while (true)
      {
        var count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token);
        if (count == 0) break;
        if (output.Length + count > CoverDiscoverer.MaximumEncodedPayloadBytes) throw new IOException("The embedded cover exceeds the supported size.");
        output.Write(buffer, 0, count);
      }
      await process.WaitForExitAsync(timeout.Token);
      var diagnostic = await errors;
      timeout.Token.ThrowIfCancellationRequested();
      if (process.ExitCode != 0) throw new IOException($"Could not extract the embedded cover: {diagnostic}");
      output.Position = 0;
      return output;
    }
    catch (Exception exception)
    {
      output.Dispose();
      if (!process.HasExited) process.Kill(true);
      await process.WaitForExitAsync(CancellationToken.None);
      try { await errors; } catch (OperationCanceledException) { }
      if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested) throw new IOException("Embedded cover extraction timed out.", exception);
      throw;
    }
  }
}
