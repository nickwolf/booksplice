namespace BookSplice.Updates.Policy;

public enum SelectionKind { None, Available, Ambiguous }

public sealed record AssetIdentity(long Id, string Name, long Length, Uri DownloadUri);

public sealed class ReleaseIdentity(long id, string tag, Uri pageUri, string notes, IReadOnlyList<AssetIdentity> assets) : IEquatable<ReleaseIdentity>
{
  public long Id { get; } = id;
  public string Tag { get; } = tag;
  public Uri PageUri { get; } = pageUri;
  public string Notes { get; } = notes;
  public IReadOnlyList<AssetIdentity> Assets { get; } = assets;

  public bool Equals(ReleaseIdentity? other) => other is not null && Id == other.Id && Tag == other.Tag &&
    PageUri == other.PageUri && Notes == other.Notes && Assets.SequenceEqual(other.Assets);

  public override bool Equals(object? obj) => obj is ReleaseIdentity other && Equals(other);

  public override int GetHashCode()
  {
    var hash = new HashCode();
    hash.Add(Id);
    hash.Add(Tag, StringComparer.Ordinal);
    hash.Add(PageUri);
    hash.Add(Notes, StringComparer.Ordinal);
    foreach (var asset in Assets)
    {
      hash.Add(asset);
    }

    return hash.ToHashCode();
  }
}

public sealed record SelectionResult(SelectionKind Kind, ReleaseCandidate? Candidate, bool Installable, string? Error, ReleaseIdentity? Identity);

public static class ReleaseSelector
{
  public static SelectionResult Select(IReadOnlyList<ReleaseCandidate> releases)
  {
    ReleaseCandidate? highest = null;
    var tied = false;
    foreach (var release in releases)
    {
      if (release.Draft || release.Prerelease || release.Version.IsPrerelease)
      {
        continue;
      }

      var comparison = highest is null ? 1 : release.Version.CompareTo(highest.Version);
      if (comparison > 0)
      {
        highest = release;
        tied = false;
      }
      else if (comparison == 0 && release.Id != highest!.Id)
      {
        tied = true;
      }
    }

    if (highest is null)
    {
      return new SelectionResult(SelectionKind.None, null, false, null, null);
    }

    if (tied)
    {
      return new SelectionResult(SelectionKind.Ambiguous, null, false, "Multiple stable releases have the same highest version precedence.", null);
    }

    var identity = new ReleaseIdentity(highest.Id, highest.Tag, highest.PageUri, highest.Notes, highest.Assets.Select(a => new AssetIdentity(a.Id, a.Name, a.Length, a.DownloadUri)).ToArray());
    var names = highest.Assets.Select(a => a.Name).ToArray();
    if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
    {
      return new SelectionResult(SelectionKind.Available, highest, false, "The release has duplicate asset names.", identity);
    }

    var version = highest.Tag[1..];
    var expected = new[]
    {
      $"BookSplice-{version}-win-x64.zip",
      $"BookSplice-{version}-win-x64.zip.sha256",
      $"BookSplice-{version}-win-x64.update.json",
      $"BookSplice-{version}-win-x64.update.sig.json"
    };
    var usable = expected.All(name => highest.Assets.Count(a => a.Name == name && a.Id > 0 && a.Length > 0 && ValidAssetUri(a.DownloadUri, highest.Tag, name)) == 1);
    return new SelectionResult(SelectionKind.Available, highest, usable, usable ? null : "The highest release is missing usable signed update assets.", identity);
  }

  private static bool ValidAssetUri(Uri uri, string tag, string name) =>
    uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com" && uri.IsDefaultPort &&
    uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
    Transport.ValidatedHttpTransport.MatchesAssetPath(uri, tag, name);
}
