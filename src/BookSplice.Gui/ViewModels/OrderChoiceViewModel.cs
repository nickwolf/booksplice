using BookSplice.Core.Ordering;

namespace BookSplice.Gui.ViewModels;

// One entry in the track order dropdown: the candidate with a name a person can read.
public sealed class OrderChoiceViewModel(OrderCandidate candidate)
{
  public OrderCandidate Candidate { get; } = candidate;
  public OrderCandidateId Id => Candidate.Id;
  public string Name => NameOf(Candidate.Id);

  // UI Automation names a data item by its text, not by DisplayMemberPath, so a screen reader would read the type name without this.
  public override string ToString() => Name;

  public static string NameOf(OrderCandidateId id) => id switch
  {
    OrderCandidateId.NaturalPath => "File and folder names",
    OrderCandidateId.Metadata => "Track numbers in metadata",
    _ => id.ToString(),
  };
}
