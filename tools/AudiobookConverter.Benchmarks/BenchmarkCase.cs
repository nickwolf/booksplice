namespace AudiobookConverter.Benchmarks;

public sealed record BenchmarkCase(string CaseId, string SourcePath, IReadOnlyList<string> Traits, IReadOnlyList<string> ExpectedOrder, IReadOnlyList<string> Groups, bool CopyPermission = true, IReadOnlyDictionary<string, string>? ExpectedProperties = null, bool ExpectedCorrupt = false);
