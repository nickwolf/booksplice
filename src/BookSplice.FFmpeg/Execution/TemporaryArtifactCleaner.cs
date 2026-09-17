using BookSplice.Core.Execution;

namespace BookSplice.FFmpeg.Execution;

public sealed class TemporaryArtifactCleaner : ITemporaryArtifactCleaner
{
  private readonly string _temporaryRoot;

  public TemporaryArtifactCleaner(string temporaryRoot)
    => _temporaryRoot = FileConversionWorkspaceFactory.NormalizeRoot(temporaryRoot);

  public Task CleanupAsync(string temporaryOutputPath, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    FileConversionWorkspaceFactory.ValidatePath(temporaryOutputPath);
    var output = Path.GetFullPath(temporaryOutputPath);
    var job = Path.GetDirectoryName(output);
    if (string.IsNullOrWhiteSpace(job) || !FileConversionWorkspaceFactory.IsContained(_temporaryRoot, job)) return Task.CompletedTask;
    var relativeJob = Path.GetRelativePath(_temporaryRoot, job);
    if (relativeJob.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0) return Task.CompletedTask;
    if (!Directory.Exists(job)) return Task.CompletedTask;
    if (FileConversionWorkspaceFactory.HasReparsePointInPath(_temporaryRoot, job)) return Task.CompletedTask;
    var entries = new DirectoryInfo(job).EnumerateFileSystemInfos().ToArray();
    if (entries.Any(entry => entry is DirectoryInfo || (entry.Attributes & FileAttributes.ReparsePoint) != 0)) return Task.CompletedTask;
    foreach (var entry in entries)
    {
      cancellationToken.ThrowIfCancellationRequested();
      entry.Delete();
    }
    Directory.Delete(job);
    return Task.CompletedTask;
  }
}
