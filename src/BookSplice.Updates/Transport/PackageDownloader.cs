using System.Security.Cryptography;
using System.Text;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Security;

namespace BookSplice.Updates.Transport;

public sealed class PackageDownloadException(string message) : Exception(message);

public sealed class PackageDownloader
{
  private readonly IReleaseClient _releases;
  private readonly ValidatedHttpTransport _transport;
  private readonly TimeSpan _idleTimeout;
  private readonly TimeSpan _stagingTimeout;

  public PackageDownloader(IReleaseClient releases, ValidatedHttpTransport transport)
    : this(releases, transport, UpdateLimits.DownloadIdleTimeout, UpdateLimits.StagingTimeout) { }

  internal PackageDownloader(IReleaseClient releases, ValidatedHttpTransport transport,
    TimeSpan idleTimeout, TimeSpan stagingTimeout)
  {
    _releases = releases;
    _transport = transport;
    _idleTimeout = idleTimeout;
    _stagingTimeout = stagingTimeout;
  }

  public async Task DownloadAsync(ReleaseCandidate release, VerifiedManifest manifest, Stream destination,
    IProgress<long>? progress, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(release);
    ArgumentNullException.ThrowIfNull(manifest);
    ArgumentNullException.ThrowIfNull(destination);
    if (!destination.CanWrite)
    {
      throw new PackageDownloadException("The package destination is not writable.");
    }

    using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    overall.CancelAfter(_stagingTimeout);
    try
    {
      var before = ReleaseSelector.Select([release]);
      if (!before.Installable || before.Identity is null || !before.Identity.Equals(manifest.ReleaseIdentity) ||
          manifest.Tag != release.Tag ||
          manifest.ArchiveName != $"BookSplice-{release.Tag[1..]}-win-x64.zip")
      {
        throw new PackageDownloadException("Selected release is not an installable signed package.");
      }

      var refreshed = await _releases.FetchAsync(overall.Token);
      var matches = refreshed.Where(candidate => candidate.Id == release.Id).Take(2).ToArray();
      var current = matches.Length == 1 ? matches[0] : null;
      var after = current is null ? null : ReleaseSelector.Select([current]);
      if (after is null || !after.Installable || !before.Identity.Equals(after.Identity))
      {
        throw new PackageDownloadException("Release changed after confirmation.");
      }

      var archive = current!.Assets.Single(asset => asset.Name == manifest.ArchiveName);
      if (archive.Length != manifest.ArchiveLength || archive.Length > UpdateLimits.ZipMaxBytes)
      {
        throw new PackageDownloadException("Archive length differs from the signed manifest.");
      }

      var checksumAsset = current.Assets.Single(asset => asset.Name == manifest.ArchiveName + ".sha256");
      using (var checksumResponse = await WithIdleAsync(token =>
        _transport.OpenAssetAsync(checksumAsset, current.Tag, token), overall.Token, cancellationToken))
      {
        if (checksumResponse.Content.Headers.ContentLength > UpdateLimits.SignatureEnvelopeMaxBytes)
        {
          throw new PackageDownloadException("Checksum companion is too large.");
        }

        await using var checksumStream = await WithIdleAsync(token =>
          checksumResponse.Content.ReadAsStreamAsync(token), overall.Token, cancellationToken);
        using var checksumBytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
          var count = await ReadWithIdleAsync(checksumStream, buffer, overall.Token, cancellationToken);
          if (count == 0)
          {
            break;
          }

          if (checksumBytes.Length + count > UpdateLimits.SignatureEnvelopeMaxBytes)
          {
            throw new PackageDownloadException("Checksum companion is too large.");
          }

          checksumBytes.Write(buffer, 0, count);
        }

        var checksum = new UTF8Encoding(false, true).GetString(checksumBytes.ToArray());
        var expectedLine = $"{manifest.ArchiveSha256}  {manifest.ArchiveName}";
        if (checksum.TrimEnd('\r', '\n') != expectedLine)
        {
          throw new PackageDownloadException("Checksum companion differs from the signed manifest.");
        }
      }

      using var response = await WithIdleAsync(token =>
        _transport.OpenAssetAsync(archive, current.Tag, token), overall.Token, cancellationToken);
      if (response.Content.Headers.ContentLength is long contentLength && contentLength != manifest.ArchiveLength)
      {
        throw new PackageDownloadException("Archive response length differs from the signed manifest.");
      }

      await using var input = await WithIdleAsync(token =>
        response.Content.ReadAsStreamAsync(token), overall.Token, cancellationToken);
      using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      var chunk = new byte[81920];
      long written = 0;
      while (true)
      {
        var count = await ReadWithIdleAsync(input, chunk, overall.Token, cancellationToken);
        if (count == 0)
        {
          break;
        }

        if (count > manifest.ArchiveLength - written)
        {
          throw new PackageDownloadException("Archive stream exceeds its signed length.");
        }

        sha.AppendData(chunk, 0, count);
        await destination.WriteAsync(chunk.AsMemory(0, count), overall.Token);
        written += count;
        progress?.Report(written);
      }

      if (written != manifest.ArchiveLength ||
          Convert.ToHexStringLower(sha.GetHashAndReset()) != manifest.ArchiveSha256)
      {
        throw new PackageDownloadException("Archive stream does not match its signed length and SHA-256.");
      }
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      throw new PackageDownloadException("Package staging exceeded its overall deadline.");
    }
    catch (IOException exception)
    {
      throw new PackageDownloadException($"Package stream was interrupted: {exception.Message}");
    }
    catch (DecoderFallbackException exception)
    {
      throw new PackageDownloadException($"Checksum companion is not valid UTF-8: {exception.Message}");
    }
  }

  private async Task<int> ReadWithIdleAsync(Stream stream, byte[] buffer, CancellationToken overall,
    CancellationToken caller)
    => await WithIdleAsync(token => stream.ReadAsync(buffer.AsMemory(), token).AsTask(), overall, caller);

  private async Task<T> WithIdleAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken overall,
    CancellationToken caller)
  {
    using var idle = CancellationTokenSource.CreateLinkedTokenSource(overall);
    idle.CancelAfter(_idleTimeout);
    try
    {
      return await operation(idle.Token);
    }
    catch (OperationCanceledException) when (!caller.IsCancellationRequested && !overall.IsCancellationRequested)
    {
      throw new PackageDownloadException("Package stream exceeded its idle deadline.");
    }
  }
}
