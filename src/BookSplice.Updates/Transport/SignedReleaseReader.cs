using BookSplice.Updates.Policy;
using BookSplice.Updates.Security;

namespace BookSplice.Updates.Transport;

public interface ISignedReleaseReader
{
  Task<VerifiedManifest> ReadAsync(ReleaseCandidate release, int settingsSchema,
    CancellationToken cancellationToken);
}

public sealed class SignedReleaseReader(IReleaseClient releases,
  ValidatedHttpTransport transport, ManifestVerifier verifier) : ISignedReleaseReader
{
  public async Task<VerifiedManifest> ReadAsync(ReleaseCandidate release, int settingsSchema,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(release);
    var selected = ReleaseSelector.Select([release]);
    if (!selected.Installable || selected.Identity is null)
      throw new ReleaseTransportException("The selected release has no usable signed update assets.");
    var refreshed = await releases.FetchAsync(cancellationToken).ConfigureAwait(false);
    var current = refreshed.Where(item => item.Id == release.Id).Take(2).ToArray();
    if (current.Length != 1 ||
        !selected.Identity.Equals(ReleaseSelector.Select([current[0]]).Identity))
      throw new ReleaseTransportException("Release changed after confirmation.");
    var stem = $"BookSplice-{release.Tag[1..]}-win-x64";
    var manifestAsset = release.Assets.Single(asset => asset.Name == stem + ".update.json");
    var signatureAsset = release.Assets.Single(asset => asset.Name == stem + ".update.sig.json");
    var manifest = await ReadBoundedAsync(manifestAsset, release.Tag,
      UpdateLimits.ManifestMaxBytes, cancellationToken).ConfigureAwait(false);
    var signature = await ReadBoundedAsync(signatureAsset, release.Tag,
      UpdateLimits.SignatureEnvelopeMaxBytes, cancellationToken).ConfigureAwait(false);
    return verifier.Verify(manifest, signature, release, settingsSchema);
  }

  private async Task<byte[]> ReadBoundedAsync(ReleaseAsset asset, string tag, long limit,
    CancellationToken cancellationToken)
  {
    if (asset.Length is <= 0 || asset.Length > limit)
      throw new ReleaseTransportException("Signed metadata asset length is invalid.");
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(UpdateLimits.ApiRequestTimeout);
    using var response = await transport.OpenAssetAsync(asset, tag, deadline.Token)
      .ConfigureAwait(false);
    if (response.Content.Headers.ContentLength is long length && length > limit)
      throw new ReleaseTransportException("Signed metadata exceeds its size limit.");
    await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token)
      .ConfigureAwait(false);
    using var buffer = new MemoryStream();
    var chunk = new byte[8192];
    while (true)
    {
      var count = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false);
      if (count == 0) break;
      if (buffer.Length + count > limit)
        throw new ReleaseTransportException("Signed metadata exceeds its size limit.");
      buffer.Write(chunk, 0, count);
    }
    if (buffer.Length != asset.Length)
      throw new ReleaseTransportException("Signed metadata differs from its release asset length.");
    return buffer.ToArray();
  }
}
