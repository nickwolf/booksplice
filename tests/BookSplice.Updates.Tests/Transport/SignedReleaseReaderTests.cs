using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Security;
using BookSplice.Updates.Transport;

namespace BookSplice.Updates.Tests.Transport;

public sealed class SignedReleaseReaderTests
{
  [Fact]
  public async Task ReadsBothBoundedAssetsAndReturnsTheVerifiedManifest()
  {
    using var key = RSA.Create(3072);
    var spki = key.ExportSubjectPublicKeyInfo();
    var keyId = Convert.ToHexStringLower(SHA256.HashData(spki));
    const string archiveName = "BookSplice-0.2.0-win-x64.zip";
    var manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
      schemaVersion = 1,
      product = "BookSplice",
      repository = "nickwolf/booksplice",
      tag = "v0.2.0",
      version = "0.2.0",
      target = "win-x64",
      archiveName,
      archiveLength = 10,
      archiveSha256 = new string('a', 64),
      packageProtocol = 1,
      settingsSchemaMin = 1,
      settingsSchemaMax = 1
    }));
    var signed = Encoding.ASCII.GetBytes("BookSplice update manifest v1\n")
      .Concat(manifest).ToArray();
    var signature = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
      schemaVersion = 1,
      keyId,
      algorithm = "RSA-PSS-SHA256",
      signature = Convert.ToBase64String(key.SignData(signed,
        HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
    }));
    var baseRelease = Candidate();
    var assets = baseRelease.Assets.Select(asset => asset with
    {
      Length = asset.Name.EndsWith(".update.json", StringComparison.Ordinal) ?
        manifest.Length : asset.Name.EndsWith(".update.sig.json", StringComparison.Ordinal) ?
          signature.Length : asset.Length
    }).ToArray();
    var release = baseRelease with { Assets = assets };
    var requests = 0;
    using var transport = new ValidatedHttpTransport(new Handler(request =>
    {
      requests++;
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(
          ".update.json", StringComparison.Ordinal) ? manifest : signature)
      };
    }), TimeProvider.System);
    var reader = new SignedReleaseReader(new FixedClient(release), transport,
      new ManifestVerifier(new Dictionary<string, byte[]> { [keyId] = spki }));
    var verified = await reader.ReadAsync(release, 1, CancellationToken.None);
    Assert.Equal("v0.2.0", verified.Tag);
    Assert.Equal(2, requests);
  }

  [Fact]
  public async Task ChangedReleaseIdentityStopsBeforeAnyAssetRequest()
  {
    var release = Candidate();
    var requests = 0;
    using var transport = new ValidatedHttpTransport(new Handler(_ =>
    {
      requests++;
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new ByteArrayContent([1])
      };
    }), TimeProvider.System);
    var changed = release with { Notes = "changed after the click" };
    var reader = new SignedReleaseReader(new FixedClient(changed), transport,
      new ManifestVerifier());
    await Assert.ThrowsAsync<ReleaseTransportException>(() =>
      reader.ReadAsync(release, 1, CancellationToken.None));
    Assert.Equal(0, requests);
  }

  [Fact]
  public async Task ManifestOverLimitIsRejectedBeforeSignatureRequest()
  {
    var release = Candidate();
    var requests = 0;
    using var transport = new ValidatedHttpTransport(new Handler(_ =>
    {
      requests++;
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new ByteArrayContent(new byte[UpdateLimits.ManifestMaxBytes + 1])
      };
    }), TimeProvider.System);
    var reader = new SignedReleaseReader(new FixedClient(release), transport,
      new ManifestVerifier());
    await Assert.ThrowsAsync<ReleaseTransportException>(() =>
      reader.ReadAsync(release, 1, CancellationToken.None));
    Assert.Equal(1, requests);
  }

  private static ReleaseCandidate Candidate()
  {
    const string tag = "v0.2.0";
    const string stem = "BookSplice-0.2.0-win-x64";
    ReleaseAsset Asset(int id, string name) => new(id, name,
      new Uri($"https://github.com/nickwolf/booksplice/releases/download/{tag}/{name}"), 10);
    return new ReleaseCandidate(1, tag, SemanticVersion.ParseTag(tag),
      new Uri("https://github.com/nickwolf/booksplice/releases/tag/v0.2.0"),
      "notes", false, false,
      [
        Asset(1, stem + ".zip"), Asset(2, stem + ".zip.sha256"),
        Asset(3, stem + ".update.json"), Asset(4, stem + ".update.sig.json")
      ]);
  }

  private sealed class FixedClient(ReleaseCandidate candidate) : IReleaseClient
  {
    public Task<IReadOnlyList<ReleaseCandidate>> FetchAsync(CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<ReleaseCandidate>>([candidate]);
  }

  private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply)
    : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
      CancellationToken cancellationToken) => Task.FromResult(reply(request));
  }
}
