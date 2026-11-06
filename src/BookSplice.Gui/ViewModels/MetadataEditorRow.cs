using BookSplice.Core.Metadata;

namespace BookSplice.Gui.ViewModels;

public sealed class MetadataEditorRow(MetadataFieldViewModel field) : ObservableModel
{
  private string _text = field.Value;
  public MetadataFieldViewModel Field { get; } = field;
  public string Name => Field.Name;
  public string State => Field.State;
  public string SourceValues => Field.Candidates;
  public bool HasSourceValues => Field.Candidates.Length > 0;
  public string Description => HasSourceValues ? $"Imported state {State}. Source values: {SourceValues}" : $"Imported state {State}. No source values.";
  public bool IsLongForm => Field.Field is SemanticField.Comment or SemanticField.Description;
  public string Text { get => _text; set { _text = value; Changed(); } }
  public bool IsChanged => _text != Field.Value;
}
