using System.Collections.ObjectModel;

namespace AudiobookConverter.Core.Metadata;

public sealed record MetadataFieldMapping(SemanticField Field, IReadOnlyList<string> Keys)
{
  public MetadataFieldMapping(SemanticField field, params string[] keys) : this(field, Array.AsReadOnly(keys)) { }
}

public sealed class MetadataProfile
{
  private readonly IReadOnlyList<MetadataFieldMapping> _mappings;

  public MetadataProfile(string name, string version, IEnumerable<MetadataFieldMapping> mappings)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    ArgumentException.ThrowIfNullOrWhiteSpace(version);
    Name = name;
    Version = version;
    _mappings = Array.AsReadOnly(mappings.Select(mapping => new MetadataFieldMapping(mapping.Field, Array.AsReadOnly(mapping.Keys.ToArray()))).ToArray());
  }

  public string Name { get; }
  public string Version { get; }
  public IReadOnlyList<MetadataFieldMapping> Mappings => _mappings;

  public IReadOnlyDictionary<string, string> Apply(BookMetadata metadata)
  {
    var output = new Dictionary<string, string>(metadata.PreservedTags, StringComparer.OrdinalIgnoreCase);
    output.Remove("SERIESPART");
    output.Remove("DISCNUMBER");

    foreach (var mapping in _mappings)
    {
      if (metadata.InputKeys.TryGetValue(mapping.Field, out var inputKeys))
      {
        foreach (var inputKey in inputKeys) output.Remove(inputKey);
      }

      var value = metadata.Get(mapping.Field);
      if (value.State is AggregationState.Missing or AggregationState.Conflicting || value.Value is null) continue;
      foreach (var key in mapping.Keys) output[key] = value.Value;
    }

    return new ReadOnlyDictionary<string, string>(output);
  }
}
