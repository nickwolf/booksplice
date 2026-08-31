using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudiobookConverter.Benchmarks;

public static class CorpusManifestLoader
{
  private static readonly JsonSerializerOptions Options = new()
  {
    PropertyNameCaseInsensitive = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
  };

  public static IReadOnlyList<BenchmarkCase> Load(string path)
  {
    try
    {
      var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Options) ?? throw Invalid("invalid manifest");
      if (string.IsNullOrWhiteSpace(manifest.SchemaVersion) || manifest.Cases is not { Count: > 0 }) throw Invalid("missing required manifest fields");
      var cases = manifest.Cases.Select(ToCase).ToArray();
      if (cases.Select(item => item.CaseId).Distinct(StringComparer.Ordinal).Count() != cases.Length) throw Invalid("duplicate case ID");
      return cases;
    }
    catch (JsonException) { throw Invalid("invalid manifest JSON"); }
  }

  private static BenchmarkCase ToCase(ManifestCase value)
  {
    if (string.IsNullOrWhiteSpace(value.CaseId) || !System.Text.RegularExpressions.Regex.IsMatch(value.CaseId, "^case-[a-z0-9-]+$")) throw Invalid("invalid case ID");
    if (string.IsNullOrWhiteSpace(value.SourcePath) || value.Traits is not { Count: > 0 } || value.ExpectedOrder is not { Count: > 0 } || value.Groups is not { Count: > 0 }) throw Invalid("missing required case fields");
    if (value.Traits.Any(string.IsNullOrWhiteSpace) || value.Groups.Any(string.IsNullOrWhiteSpace) || value.ExpectedOrder.Any(name => string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name) || value.ExpectedOrder.Distinct(StringComparer.Ordinal).Count() != value.ExpectedOrder.Count) throw Invalid("invalid expected order or collection values");
    if (value.Traits.Distinct(StringComparer.Ordinal).Count() != value.Traits.Count || value.Groups.Distinct(StringComparer.Ordinal).Count() != value.Groups.Count) throw Invalid("duplicate collection values");
    return new BenchmarkCase(value.CaseId, value.SourcePath, value.Traits, value.ExpectedOrder, value.Groups, value.CopyPermission, value.ExpectedProperties, value.ExpectedCorrupt);
  }

  private static InvalidDataException Invalid(string detail) => new($"Corpus manifest rejected: {detail}.");

  private sealed class Manifest
  {
    public string? SchemaVersion { get; init; }
    public string? GeneratedBy { get; init; }
    public List<ManifestCase>? Cases { get; init; }
  }

  private sealed class ManifestCase
  {
    public string? CaseId { get; init; }
    public string? SourcePath { get; init; }
    public List<string>? Traits { get; init; }
    public List<string>? ExpectedOrder { get; init; }
    public List<string>? Groups { get; init; }
    public bool CopyPermission { get; init; }
    public Dictionary<string, string>? ExpectedProperties { get; init; }
    public bool ExpectedCorrupt { get; init; }
    public List<string>? Arguments { get; init; }
  }
}
