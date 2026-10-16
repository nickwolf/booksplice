using System.Security.Cryptography;
using System.Text;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Security;

namespace BookSplice.Updates.Tests.Security;

public sealed class ManifestVerifierTests
{
  private const string ValidJson = """{"schemaVersion":1,"product":"BookSplice","repository":"nickwolf/booksplice","tag":"v1.2.3","version":"1.2.3","target":"win-x64","archiveName":"BookSplice-1.2.3-win-x64.zip","archiveLength":3,"archiveSha256":"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad","packageProtocol":1,"settingsSchemaMin":1,"settingsSchemaMax":1}""";

  [Fact]
  public void SignedBytesAreVerifiedWithoutReserialization()
  {
    using var key = RSA.Create(3072);
    var spki = key.ExportSubjectPublicKeyInfo();
    var keyId = Convert.ToHexStringLower(SHA256.HashData(spki));
    var release = new ReleaseCandidate(1, "v1.2.3", SemanticVersion.ParseTag("v1.2.3"),
      new Uri("https://github.com/nickwolf/booksplice/releases/tag/v1.2.3"), "notes", false, false,
      [new ReleaseAsset(2, "BookSplice-1.2.3-win-x64.zip", new Uri("https://github.com/nickwolf/booksplice/releases/download/v1.2.3/BookSplice-1.2.3-win-x64.zip"), 3)]);
    var manifest = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"product":"BookSplice","repository":"nickwolf/booksplice","tag":"v1.2.3","version":"1.2.3","target":"win-x64","archiveName":"BookSplice-1.2.3-win-x64.zip","archiveLength":3,"archiveSha256":"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad","packageProtocol":1,"settingsSchemaMin":1,"settingsSchemaMax":1}""");
    var domain = Encoding.ASCII.GetBytes("BookSplice update manifest v1\n");
    var message = new byte[domain.Length + manifest.Length];
    domain.CopyTo(message, 0);
    manifest.CopyTo(message, domain.Length);
    var signature = key.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    var envelope = Encoding.UTF8.GetBytes($$"""{"schemaVersion":1,"keyId":"{{keyId}}","algorithm":"RSA-PSS-SHA256","signature":"{{Convert.ToBase64String(signature)}}"}""");
    var verifier = new ManifestVerifier(new Dictionary<string, byte[]> { [keyId] = spki });
    Assert.Equal(3, verifier.Verify(manifest, envelope, release, 1).ArchiveLength);
    manifest[^2] ^= 1;
    Assert.Throws<ManifestVerificationException>(() => verifier.Verify(manifest, envelope, release, 1));
  }

  [Theory]
  [InlineData("\"repository\":\"nickwolf/booksplice\"", "\"repository\":\"other/booksplice\"")]
  [InlineData("\"tag\":\"v1.2.3\"", "\"tag\":\"v1.2.4\"")]
  [InlineData("\"target\":\"win-x64\"", "\"target\":\"linux-x64\"")]
  [InlineData("\"packageProtocol\":1", "\"packageProtocol\":2")]
  [InlineData("\"settingsSchemaMin\":1", "\"settingsSchemaMin\":2")]
  [InlineData("\"product\":\"BookSplice\"", "\"product\":\"Other\"")]
  public void SignedButMismatchedManifestFails(string original, string changed)
  {
    using var fixture = new Fixture();
    var bytes = Encoding.UTF8.GetBytes(ValidJson.Replace(original, changed, StringComparison.Ordinal));
    Assert.Throws<ManifestVerificationException>(() => fixture.Verify(bytes));
  }

  [Fact]
  public void DuplicateAndUnknownFieldsFailEvenWhenSigned()
  {
    using var fixture = new Fixture();
    Assert.Throws<ManifestVerificationException>(() => fixture.Verify(Encoding.UTF8.GetBytes(
      ValidJson.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"schemaVersion\":1,", StringComparison.Ordinal))));
    Assert.Throws<ManifestVerificationException>(() => fixture.Verify(Encoding.UTF8.GetBytes(
      ValidJson.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"extra\":1,", StringComparison.Ordinal))));
  }

  [Fact]
  public void InvalidUtf8AndOversizedPayloadFail()
  {
    using var fixture = new Fixture();
    Assert.Throws<ManifestVerificationException>(() => fixture.Verify([0xff]));
    Assert.Throws<ManifestVerificationException>(() => fixture.Verify(new byte[UpdateLimits.ManifestMaxBytes + 1]));
  }

  [Theory]
  [InlineData("\"archiveSha256\":\"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\"", "\"archiveSha256\":null")]
  [InlineData("\"product\":\"BookSplice\"", "\"product\":null")]
  [InlineData("\"target\":\"win-x64\"", "\"target\":123")]
  public void SignedNullOrWrongTypeIsTypedFailure(string original, string changed)
  {
    using var fixture = new Fixture();
    Assert.Throws<ManifestVerificationException>(() => fixture.Verify(Encoding.UTF8.GetBytes(
      ValidJson.Replace(original, changed, StringComparison.Ordinal))));
  }

  [Fact]
  public void WrongKeyIdKeyAndAlgorithmFail()
  {
    using var fixture = new Fixture();
    var manifest = Encoding.UTF8.GetBytes(ValidJson);
    var envelope = fixture.Envelope(manifest);
    Assert.Throws<ManifestVerificationException>(() => fixture.Verifier.Verify(manifest,
      Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(envelope).Replace(fixture.KeyId, new string('0', 64), StringComparison.Ordinal)),
      fixture.Release, 1));
    Assert.Throws<ManifestVerificationException>(() => fixture.Verifier.Verify(manifest,
      Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(envelope).Replace("RSA-PSS-SHA256", "RSA-PKCS1-SHA256", StringComparison.Ordinal)),
      fixture.Release, 1));
    using var other = new Fixture();
    Assert.Throws<ManifestVerificationException>(() => other.Verifier.Verify(manifest, envelope, other.Release, 1));
  }

  [Fact]
  public void LiteralBackslashNAndPkcs1SignatureFail()
  {
    using var fixture = new Fixture();
    var manifest = Encoding.UTF8.GetBytes(ValidJson);
    Assert.Throws<ManifestVerificationException>(() => fixture.Verifier.Verify(manifest,
      fixture.Envelope(manifest, Encoding.ASCII.GetBytes("BookSplice update manifest v1\\n")), fixture.Release, 1));
    Assert.Throws<ManifestVerificationException>(() => fixture.Verifier.Verify(manifest,
      fixture.Envelope(manifest, padding: RSASignaturePadding.Pkcs1), fixture.Release, 1));
  }

  [Fact]
  public void VerificationCapturesReleaseIdentityWithoutMutableAssetList()
  {
    using var fixture = new Fixture();
    var verified = fixture.Verify(Encoding.UTF8.GetBytes(ValidJson));
    Assert.IsNotType<AssetIdentity[]>(verified.ReleaseIdentity.Assets);
    Assert.Equal(fixture.Release.Assets[0].Id, verified.ReleaseIdentity.Assets[0].Id);
  }

  private sealed class Fixture : IDisposable
  {
    private readonly RSA _key = RSA.Create(3072);
    public ReleaseCandidate Release { get; } = new(1, "v1.2.3", SemanticVersion.ParseTag("v1.2.3"),
      new Uri("https://github.com/nickwolf/booksplice/releases/tag/v1.2.3"), "notes", false, false,
      [new ReleaseAsset(2, "BookSplice-1.2.3-win-x64.zip",
        new Uri("https://github.com/nickwolf/booksplice/releases/download/v1.2.3/BookSplice-1.2.3-win-x64.zip"), 3)]);

    public string KeyId => Convert.ToHexStringLower(SHA256.HashData(_key.ExportSubjectPublicKeyInfo()));

    public ManifestVerifier Verifier => new(new Dictionary<string, byte[]> { [KeyId] = _key.ExportSubjectPublicKeyInfo() });

    public VerifiedManifest Verify(byte[] manifest) => Verifier.Verify(manifest, Envelope(manifest), Release, 1);

    public byte[] Envelope(byte[] manifest, byte[]? domain = null, RSASignaturePadding? padding = null)
    {
      domain ??= Encoding.ASCII.GetBytes("BookSplice update manifest v1\n");
      var message = domain.Concat(manifest).ToArray();
      var signature = _key.SignData(message, HashAlgorithmName.SHA256, padding ?? RSASignaturePadding.Pss);
      return Encoding.UTF8.GetBytes($$"""{"schemaVersion":1,"keyId":"{{KeyId}}","algorithm":"RSA-PSS-SHA256","signature":"{{Convert.ToBase64String(signature)}}"}""");
    }

    public void Dispose() => _key.Dispose();
  }
}
