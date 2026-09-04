using AudiobookConverter.Core.Planning;
using AudiobookConverter.Core.Validation;

namespace AudiobookConverter.Core.Naming;

public sealed class AtomicPublisher : IAtomicPublisher
{
  public async Task<PublicationResult> PublishAsync(ConversionPlan plan, ValidationReport report, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(plan);
    ArgumentNullException.ThrowIfNull(report);
    cancellationToken.ThrowIfCancellationRequested();
    var temporaryPath = Path.GetFullPath(report.TemporaryOutputPath);
    if (!report.IsValid) return Rejected("Publication requires a successful validation report.");
    if (!File.Exists(temporaryPath)) return Rejected("The validated temporary output is unavailable.");

    var finalPath = Path.GetFullPath(plan.OutputPath);
    var destination = Path.GetDirectoryName(finalPath);
    if (string.IsNullOrWhiteSpace(destination)) return Rejected("The output destination is invalid.");
    Directory.CreateDirectory(destination);
    var partialPath = Path.Combine(destination, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.partial");
    try
    {
      await CopyDurablyAsync(temporaryPath, partialPath, cancellationToken).ConfigureAwait(false);
      cancellationToken.ThrowIfCancellationRequested();
      var published = plan.CollisionPolicy == CollisionPolicy.Overwrite
        ? PublishOverwrite(partialPath, finalPath)
        : PublishAvoidingCollisions(partialPath, finalPath, cancellationToken);
      File.Delete(temporaryPath);
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

  private static async Task CopyDurablyAsync(string source, string partial, CancellationToken cancellationToken)
  {
    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
    await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    output.Flush(flushToDisk: true);
  }

  private static string PublishOverwrite(string partialPath, string finalPath)
  {
    File.Move(partialPath, finalPath, overwrite: true);
    return finalPath;
  }

  private static string PublishAvoidingCollisions(string partialPath, string requestedPath, CancellationToken cancellationToken)
  {
    var candidate = requestedPath;
    for (var ordinal = 2; ; ordinal++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        File.Move(partialPath, candidate, overwrite: false);
        return candidate;
      }
      catch (IOException) when (File.Exists(candidate))
      {
        candidate = WithCollisionSuffix(requestedPath, ordinal);
      }
    }
  }

  private static string WithCollisionSuffix(string path, int ordinal)
    => Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} ({ordinal}){Path.GetExtension(path)}");

  private static PublicationResult Rejected(string diagnostic) => new(PublicationStatus.Rejected, null, [diagnostic]);
  private static void DeleteOwnedPartial(string path) { try { File.Delete(path); } catch { } }
}
