namespace BookSplice.Updates.Tests.Storage;

internal static class TestDirectory
{
  // A process that has just exited, or a scanner looking at its files, can hold them for a moment after exit.
  public static void Delete(string path, TimeSpan? patience = null)
  {
    var deadline = DateTime.UtcNow + (patience ?? TimeSpan.FromSeconds(5));
    while (true)
    {
      try
      {
        if (Directory.Exists(path)) Directory.Delete(path, true);
        return;
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
      {
        Thread.Sleep(100);
      }
    }
  }

  // For cleanup after a failure that is already being reported.
  public static void TryDelete(string path)
  {
    try { Delete(path); }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
  }
}
