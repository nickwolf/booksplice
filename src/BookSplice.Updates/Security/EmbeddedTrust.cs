namespace BookSplice.Updates.Security;

public static class EmbeddedTrust
{
  // No production release key has been provisioned. Verification stays closed until one is embedded.
  internal static IReadOnlyDictionary<string, byte[]> PublicKeys { get; } =
    new Dictionary<string, byte[]>(StringComparer.Ordinal);
}
