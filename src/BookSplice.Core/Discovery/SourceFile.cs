using BookSplice.Core.Analysis;

namespace BookSplice.Core.Discovery;

public sealed record SourceFile(string FullPath, string RelativePath, MediaProbeResult ProbeResult);
