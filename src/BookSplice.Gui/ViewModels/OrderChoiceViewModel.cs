using BookSplice.Core.Ordering;

namespace BookSplice.Gui.ViewModels;

// One entry in the track order dropdown: the candidate with a name a person can read.
public sealed class OrderChoiceViewModel(OrderCandidate candidate)
{
  public OrderCandidate Candidate { get; } = candidate;
  public OrderCandidateId Id => Candidate.Id;
  public string Name => NameOf(Candidate.Id);

  public static string NameOf(OrderCandidateId id) => id switch
  {
    OrderCandidateId.NaturalPath => "File and folder names",
    OrderCandidateId.Metadata => "Track numbers in metadata",
    _ => id.ToString(),
  };
}
