namespace AudiobookConverter.Core.Execution;

public interface ITemporaryArtifactCleaner
{
  Task CleanupAsync(string temporaryOutputPath, CancellationToken cancellationToken);
}
