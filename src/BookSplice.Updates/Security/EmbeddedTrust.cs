using System.Security.Cryptography;

namespace BookSplice.Updates.Security;

public static class EmbeddedTrust
{
  // Release builds embed the reviewed production public key; Test builds embed at most a disposable test key.
  // Without an embedded key, verification stays closed.
  internal static IReadOnlyDictionary<string, byte[]> PublicKeys { get; } = Load();

  private static Dictionary<string, byte[]> Load()
  {
    var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    using var stream = typeof(EmbeddedTrust).Assembly.GetManifestResourceStream("BookSplice.Trust.pem");
    if (stream is null)
    {
      return UpdateBuild.Flavor == BuildFlavor.Release
        ? throw new InvalidOperationException("Release build is missing its embedded public key.")
        : keys;
    }

    using var reader = new StreamReader(stream);
    using var rsa = RSA.Create();
    rsa.ImportFromPem(reader.ReadToEnd());
    var spki = rsa.ExportSubjectPublicKeyInfo();
    keys[Convert.ToHexStringLower(SHA256.HashData(spki))] = spki;
    return keys;
  }
}
