namespace BookSplice.Updates.Policy;

public static class UpdateLimits
{
    public const int ReleasePageSize = 100;
    public const int MaxReleasePages = 10;
    public static readonly TimeSpan ApiRequestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan StagingTimeout = TimeSpan.FromMinutes(30);
    public const long ReleasePageMaxBytes = 2L * 1024 * 1024;
    public const long NotesMaxBytes = 256L * 1024;
    public const long ManifestMaxBytes = 1L * 1024 * 1024;
    public const long SignatureEnvelopeMaxBytes = 16L * 1024;
    public const long ZipMaxBytes = 1L * 1024 * 1024 * 1024;
    public const int MaxArchiveEntries = 20_000;
    public const long MaxExtractedFileBytes = 1L * 1024 * 1024 * 1024;
    public const long MaxExtractedTotalBytes = 4L * 1024 * 1024 * 1024;
    public static readonly TimeSpan SelfCheckTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(60);
}
