using BookSplice.Core.Planning;
using BookSplice.Core.Validation;

namespace BookSplice.Core.Naming;

public interface IAtomicPublisherFileOperations
{
  Task CopyDurablyAsync(string source, string partialPath, CancellationToken cancellationToken);
  void CreateDirectory(string path);
  void Delete(string path);
  bool Exists(string path);
  void Move(string source, string destination, bool overwrite);
}

public sealed class FileSystemAtomicPublisherFileOperations : IAtomicPublisherFileOperations
{
  public async Task CopyDurablyAsync(string source, string partialPath, CancellationToken cancellationToken)
  {
    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
    await using var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    output.Flush(flushToDisk: true);
  }

  public void CreateDirectory(string path) => Directory.CreateDirectory(path);
  public void Delete(string path) => File.Delete(path);
  public bool Exists(string path) => File.Exists(path);
  public void Move(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);
}


public sealed class AtomicPublisher : IAtomicPublisher
{
  private readonly IAtomicPublisherFileOperations _operations;

  public AtomicPublisher(IAtomicPublisherFileOperations? operations = null)
    => _operations = operations ?? new FileSystemAtomicPublisherFileOperations();

  public async Task<PublicationResult> PublishAsync(ConversionPlan plan, ValidationReport report, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(plan);
    ArgumentNullException.ThrowIfNull(report);
    cancellationToken.ThrowIfCancellationRequested();
    var temporaryPath = Path.GetFullPath(report.TemporaryOutputPath);
    if (!report.IsValid) return Rejected("Publication requires a successful validation report.");
    if (report.PlanId != plan.PlanId) return Rejected("Publication requires validation for this exact conversion plan.");
    if (!_operations.Exists(temporaryPath)) return Rejected("The validated temporary output is unavailable.");

    var finalPath = Path.GetFullPath(plan.OutputPath);
    var destination = Path.GetDirectoryName(finalPath);
    if (string.IsNullOrWhiteSpace(destination)) return Rejected("The output destination is invalid.");
    _operations.CreateDirectory(destination);
    var partialPath = Path.Combine(destination, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.partial");
    try
    {
      await _operations.CopyDurablyAsync(temporaryPath, partialPath, cancellationToken).ConfigureAwait(false);
      cancellationToken.ThrowIfCancellationRequested();
      var published = plan.CollisionPolicy == CollisionPolicy.Overwrite
        ? PublishOverwrite(partialPath, finalPath)
        : PublishAvoidingCollisions(partialPath, finalPath, cancellationToken);
      _operations.Delete(temporaryPath);
      return new PublicationResult(PublicationStatus.Published, published);
    }
    catch (OperationCanceledException)
    {
      DeleteOwnedPartial(partialPath);
      throw;
    }
    catch (Exception)
    {
      DeleteOwnedPartial(partialPath);
      return new PublicationResult(PublicationStatus.Failed, null, ["The validated output could not be published."]);
    }
  }


  private string PublishOverwrite(string partialPath, string finalPath)
  {
    _operations.Move(partialPath, finalPath, overwrite: true);
    return finalPath;
  }

  private string PublishAvoidingCollisions(string partialPath, string requestedPath, CancellationToken cancellationToken)
  {
    var candidate = requestedPath;
    for (var ordinal = 2; ; ordinal++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        _operations.Move(partialPath, candidate, overwrite: false);
        return candidate;
      }
      catch (IOException) when (_operations.Exists(candidate))
      {
        candidate = WithCollisionSuffix(requestedPath, ordinal);
      }
    }
  }

  private static string WithCollisionSuffix(string path, int ordinal)
    => Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} ({ordinal}){Path.GetExtension(path)}");

  private static PublicationResult Rejected(string diagnostic) => new(PublicationStatus.Rejected, null, [diagnostic]);
  private void DeleteOwnedPartial(string path) { try { _operations.Delete(path); } catch { } }
}
