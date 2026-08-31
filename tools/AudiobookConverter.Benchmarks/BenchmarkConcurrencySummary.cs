namespace AudiobookConverter.Benchmarks;

public static class BenchmarkConcurrencySummary
{
  public static decimal AverageAggregateRealtimeFactor(IEnumerable<BenchmarkResult> rows, int concurrency)
  {
    var rounds = rows.Where(row => row.Operation == "concurrency" && row.Concurrency == concurrency).GroupBy(row => row.RoundId, StringComparer.Ordinal).Select(group => group.First().AggregateRealtimeFactor ?? throw new InvalidDataException("concurrency round is missing aggregate realtime factor")).ToArray();
    return rounds.Length == 0 ? throw new InvalidDataException("no concurrency rounds found") : rounds.Average();
  }

  public static decimal PercentImprovement(IEnumerable<BenchmarkResult> rows, int baselineConcurrency, int candidateConcurrency)
  {
    var baseline = AverageAggregateRealtimeFactor(rows, baselineConcurrency);
    return (AverageAggregateRealtimeFactor(rows, candidateConcurrency) / baseline - 1m) * 100m;
  }

  public static int RecommendConcurrency(IEnumerable<BenchmarkResult> rows, IReadOnlyList<int> testedLevels, decimal materialImprovementPercent = 10m)
  {
    if (testedLevels.Count == 0) throw new InvalidDataException("no tested concurrency levels");
    var recommendation = testedLevels[0];
    for (var index = 1; index < testedLevels.Count; index++)
      if (PercentImprovement(rows, testedLevels[index - 1], testedLevels[index]) >= materialImprovementPercent) recommendation = testedLevels[index];
      else break;
    return recommendation;
  }
}
