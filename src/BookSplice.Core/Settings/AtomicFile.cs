namespace BookSplice.Core.Settings;

internal static class AtomicFile
{
  // The temp file sits beside the target so the final Replace or Move stays on one volume.
  public static async Task WriteAsync(string path, Func<Stream, Task> write, CancellationToken cancellationToken = default)
  {
    var directory = Path.GetDirectoryName(path)!;
    Directory.CreateDirectory(directory);
    var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    try
    {
      await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
      {
        await write(stream);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
      }
      cancellationToken.ThrowIfCancellationRequested();
      if (File.Exists(path)) File.Replace(tempPath, path, null);
      else File.Move(tempPath, path);
    }
    finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
  }

  public static void Write(string path, byte[] content)
    => WriteAsync(path, stream => stream.WriteAsync(content).AsTask()).GetAwaiter().GetResult();

  public static void DeleteLeftovers(string directory, string fileName)
  {
    if (!Directory.Exists(directory)) return;
    foreach (var leftover in Directory.EnumerateFiles(directory, $".{fileName}.*.tmp"))
    {
      try { File.Delete(leftover); }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
  }
}
