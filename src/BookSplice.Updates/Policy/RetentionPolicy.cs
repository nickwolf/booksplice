using System.Runtime.Versioning;
using BookSplice.Updates.Storage;

namespace BookSplice.Updates.Policy;

public sealed record ProtectedTargets(IReadOnlyCollection<string> Paths);

// Pure selection: which recorded directories may be discarded. Ownership and identity are
// verified later, by the cleanup that acts on the result.
[SupportedOSPlatform("windows")]
public static class RetentionPolicy
{
  // Structural directories (managed-root, records, versions, staging, trash) are never listed here.
  private static readonly string[] DiscardableKinds = ["version", "stage", "payload"];

  public static IReadOnlyList<OwnedDirectory> Select(IReadOnlyList<OwnedDirectory> owned,
    ProtectedTargets protectedTargets)
  {
    ArgumentNullException.ThrowIfNull(owned);
    ArgumentNullException.ThrowIfNull(protectedTargets);
    var keep = new HashSet<string>(protectedTargets.Paths, StringComparer.OrdinalIgnoreCase);
    return owned.Where(directory => DiscardableKinds.Contains(directory.Kind) &&
      !keep.Contains(directory.Path)).ToArray();
  }
}
