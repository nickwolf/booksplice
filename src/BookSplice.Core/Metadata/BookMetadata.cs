namespace BookSplice.Core.Metadata;

public sealed class BookMetadata
{
  public BookMetadata(
    IReadOnlyDictionary<SemanticField, AggregatedValue> fields,
    IReadOnlyDictionary<string, string> preservedTags,
    IReadOnlyDictionary<SemanticField, IReadOnlyList<string>> inputKeys)
  {
    Fields = fields;
    PreservedTags = preservedTags;
    InputKeys = inputKeys;
  }

  public IReadOnlyDictionary<SemanticField, AggregatedValue> Fields { get; }
  public IReadOnlyDictionary<string, string> PreservedTags { get; }
  public IReadOnlyDictionary<SemanticField, IReadOnlyList<string>> InputKeys { get; }

  public AggregatedValue Get(SemanticField field)
    => Fields.TryGetValue(field, out var value) ? value : AggregatedValue.Missing(field);
}
