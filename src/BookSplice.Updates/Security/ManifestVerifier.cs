using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookSplice.Updates.Policy;

namespace BookSplice.Updates.Security;

public sealed class ManifestVerificationException(string message) : Exception(message);

public sealed class ManifestVerifier
{
  private static readonly byte[] Domain = Encoding.ASCII.GetBytes("BookSplice update manifest v1\n");
  private readonly IReadOnlyDictionary<string, byte[]> _keys;

  public ManifestVerifier() : this(EmbeddedTrust.PublicKeys) { }

  internal ManifestVerifier(IReadOnlyDictionary<string, byte[]> keys) => _keys = keys;

  public VerifiedManifest Verify(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> envelope,
    ReleaseCandidate release, int settingsSchema)
  {
    if (manifest.Length == 0 || manifest.Length > UpdateLimits.ManifestMaxBytes ||
        envelope.Length == 0 || envelope.Length > UpdateLimits.SignatureEnvelopeMaxBytes)
    {
      throw new ManifestVerificationException("Signed metadata exceeds its size limit.");
    }

    var manifestBytes = manifest.ToArray();
    var envelopeBytes = envelope.ToArray();
    try
    {
      using var signed = ParseObject(envelopeBytes, ["schemaVersion", "keyId", "algorithm", "signature"]);
      var fields = signed.RootElement;
      if (fields.GetProperty("schemaVersion").GetInt32() != 1 ||
          fields.GetProperty("algorithm").GetString() != "RSA-PSS-SHA256")
      {
        throw new ManifestVerificationException("Signature envelope has an unsupported schema or algorithm.");
      }

      var keyId = fields.GetProperty("keyId").GetString();
      if (keyId is null || !_keys.TryGetValue(keyId, out var spki) ||
          keyId != Convert.ToHexStringLower(SHA256.HashData(spki)))
      {
        throw new ManifestVerificationException("Signature key is not trusted.");
      }

      var signature = Convert.FromBase64String(fields.GetProperty("signature").GetString() ?? "");
      using var rsa = RSA.Create();
      rsa.ImportSubjectPublicKeyInfo(spki, out var read);
      if (read != spki.Length || rsa.KeySize != 3072 || signature.Length != 384)
      {
        throw new ManifestVerificationException("Signature key or length is invalid.");
      }

      var message = new byte[Domain.Length + manifestBytes.Length];
      Domain.CopyTo(message, 0);
      manifestBytes.CopyTo(message, Domain.Length);
      if (!rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
      {
        throw new ManifestVerificationException("Manifest signature did not verify.");
      }

      using var parsed = ParseObject(manifestBytes,
        ["schemaVersion", "product", "repository", "tag", "version", "target", "archiveName",
         "archiveLength", "archiveSha256", "packageProtocol", "settingsSchemaMin", "settingsSchemaMax"]);
      var value = parsed.RootElement;
      var schema = value.GetProperty("schemaVersion").GetInt32();
      var product = RequiredString(value, "product");
      var repository = RequiredString(value, "repository");
      var tag = RequiredString(value, "tag");
      var version = RequiredString(value, "version");
      var target = RequiredString(value, "target");
      var archiveName = RequiredString(value, "archiveName");
      var length = value.GetProperty("archiveLength").GetInt64();
      var hash = RequiredString(value, "archiveSha256");
      var protocol = value.GetProperty("packageProtocol").GetInt32();
      var min = value.GetProperty("settingsSchemaMin").GetInt32();
      var max = value.GetProperty("settingsSchemaMax").GetInt32();
      var expectedName = $"BookSplice-{release.Tag[1..]}-win-x64.zip";
      if (schema != 1 || product != "BookSplice" || repository != "nickwolf/booksplice" ||
          tag != release.Tag || version != release.Tag[1..] || release.Version.IsPrerelease ||
          SemanticVersion.ParseTag(tag).IsPrerelease || target != "win-x64" ||
          archiveName != expectedName || length <= 0 || length > UpdateLimits.ZipMaxBytes ||
          hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
          protocol != 1 || min < 0 || max < min || settingsSchema < min || settingsSchema > max ||
          release.Assets.Count(a => a.Name == archiveName && a.Length == length && a.Id > 0) != 1)
      {
        throw new ManifestVerificationException("Manifest does not match the selected release or installation.");
      }

      var identity = new ReleaseIdentity(release.Id, release.Tag, release.PageUri, release.Notes,
        Array.AsReadOnly(release.Assets.Select(a => new AssetIdentity(a.Id, a.Name, a.Length, a.DownloadUri)).ToArray()));
      return new VerifiedManifest(schema, product, repository, tag, version, target, archiveName,
        length, hash, protocol, min, max, identity);
    }
    catch (Exception exception) when (exception is JsonException or DecoderFallbackException or FormatException or
      InvalidOperationException or KeyNotFoundException or CryptographicException or ArgumentException)
    {
      throw new ManifestVerificationException($"Signed metadata is invalid: {exception.Message}");
    }
  }

  private static string RequiredString(JsonElement value, string name) =>
    value.GetProperty(name).GetString() ?? throw new ManifestVerificationException($"Signed metadata field {name} is null.");

  private static JsonDocument ParseObject(ReadOnlySpan<byte> bytes, string[] allowed)
  {
    // JsonDocument accepts duplicate names, so check the exact flat schema before reading values.
    _ = new UTF8Encoding(false, true).GetString(bytes);
    var document = JsonDocument.Parse(bytes.ToArray());
    try
    {
      if (document.RootElement.ValueKind != JsonValueKind.Object)
      {
        throw new ManifestVerificationException("Signed metadata must be one JSON object.");
      }

      var names = new HashSet<string>(StringComparer.Ordinal);
      foreach (var property in document.RootElement.EnumerateObject())
      {
        if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
        {
          throw new ManifestVerificationException("Signed metadata has duplicate or unknown fields.");
        }
      }

      if (names.Count != allowed.Length)
      {
        throw new ManifestVerificationException("Signed metadata has missing fields.");
      }

      return document;
    }
    catch
    {
      document.Dispose();
      throw;
    }
  }
}
