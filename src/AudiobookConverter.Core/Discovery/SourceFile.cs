using AudiobookConverter.Core.Analysis;

namespace AudiobookConverter.Core.Discovery;

public sealed record SourceFile(string FullPath, string RelativePath, MediaProbeResult ProbeResult);
