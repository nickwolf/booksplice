using BookSplice.Core.Metadata;

namespace BookSplice.Gui.ViewModels;

public sealed class MetadataFieldViewModel(SemanticField field, AggregatedValue imported) : ObservableModel
{
  private string _value = imported.Value ?? "";
  public SemanticField Field { get; } = field;
  public string Name => Field.ToString();
  public string State => imported.State.ToString();
  public string Candidates => string.Join(" | ", imported.Candidates.Select(candidate => candidate.Value).Distinct());
  public bool IsEdited { get; private set; }
  public string Value
  {
    get => _value;
    set { _value = value; IsEdited = true; Changed(); }
  }
}
