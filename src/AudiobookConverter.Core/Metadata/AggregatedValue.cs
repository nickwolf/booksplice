namespace AudiobookConverter.Core.Metadata;

public sealed record MetadataCandidate(string Value, string SourceFile, string SourceKey);

public sealed record AggregatedValue(
  SemanticField Field,
  AggregationState State,
  string? Value,
  IReadOnlyList<MetadataCandidate> Candidates)
{
  public static AggregatedValue Missing(SemanticField field) => new(field, AggregationState.Missing, null, []);
}
