namespace BookSplice.Core.Ordering;

public enum OrderStatus { Resolved, NeedsDecision }

public sealed class OrderResolution
{
  public OrderResolution(OrderStatus status, OrderCandidate? selectedCandidate, IEnumerable<OrderCandidate> candidates, IEnumerable<OrderEvidence> evidence)
  {
    Status = status;
    SelectedCandidate = selectedCandidate;
    Candidates = Array.AsReadOnly(candidates.ToArray());
    AlternateCandidates = Array.AsReadOnly(Candidates.Where(candidate => candidate != selectedCandidate).ToArray());
    Evidence = Array.AsReadOnly(evidence.ToArray());
  }
  public OrderStatus Status { get; }
  public OrderCandidate? SelectedCandidate { get; }
  public IReadOnlyList<OrderCandidate> Candidates { get; }
  public IReadOnlyList<OrderCandidate> AlternateCandidates { get; }
  public IReadOnlyList<OrderEvidence> Evidence { get; }
}
