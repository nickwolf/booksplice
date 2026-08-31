namespace AudiobookConverter.Core.Ordering;

public enum OrderCandidateId { NaturalPath, Metadata }
public enum OrderConfidence { Low, Medium, High }
public sealed record OrderEvidence(string Code, IReadOnlyList<string> FileIds);

public sealed class OrderCandidate
{
  public OrderCandidate(OrderCandidateId id, IEnumerable<string> fileIds, bool isCredible, OrderConfidence confidence, IEnumerable<OrderEvidence> evidence)
  {
    Id = id;
    FileIds = Array.AsReadOnly(fileIds.ToArray());
    IsCredible = isCredible;
    Confidence = confidence;
    Evidence = Array.AsReadOnly(evidence.ToArray());
  }
  public OrderCandidateId Id { get; }
  public IReadOnlyList<string> FileIds { get; }
  public bool IsCredible { get; }
  public OrderConfidence Confidence { get; }
  public IReadOnlyList<OrderEvidence> Evidence { get; }
}
