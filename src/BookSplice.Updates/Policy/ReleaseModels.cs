namespace BookSplice.Updates.Policy;

public sealed record ReleaseAsset(long Id, string Name, Uri DownloadUri, long Length);

public sealed record ReleaseCandidate(
    long Id,
    string Tag,
    SemanticVersion Version,
    Uri PageUri,
    string Notes,
    bool Draft,
    bool Prerelease,
    IReadOnlyList<ReleaseAsset> Assets);
