using System.Text;
using System.Text.Json;

namespace BookSplice.Updates.Handover;

public sealed record SelfCheckEntry(bool Ok, string? Detail);

public sealed record SelfCheckReport(int SchemaVersion, string Product, string Version,
  string Target, int ProtocolVersion, Dictionary<string, SelfCheckEntry> Checks)
{
  private static readonly JsonSerializerOptions StrictJson = new()
  {
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
  };
  public const int MaximumBytes = 16 * 1024;
  public static readonly string[] RequiredChecks =
  [
    "packagedIdentity", "payloadInventory", "runtime", "wpfRendered",
    "ffmpeg", "ffprobe", "settingsSchema"
  ];

  public void Validate(string expectedVersion)
  {
    if (SchemaVersion != 1 || Product != "BookSplice" || Version != expectedVersion ||
        Target != "win-x64" || ProtocolVersion != UpdateProtocol.ProtocolVersion ||
        Checks is null || Checks.Count != RequiredChecks.Length ||
        RequiredChecks.Any(name => !Checks.TryGetValue(name, out var entry) || entry is null || !entry.Ok))
      throw new InvalidDataException("The packaged self-check did not pass every required check.");
  }

  public static SelfCheckReport Parse(ReadOnlySpan<byte> bytes, string expectedVersion)
  {
    if (bytes.Length is 0 or > MaximumBytes) throw new InvalidDataException("Self-check report size is invalid.");
    _ = new UTF8Encoding(false, true).GetString(bytes);
    using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
    RejectDuplicates(document.RootElement);
    if (document.RootElement.ValueKind != JsonValueKind.Object ||
        document.RootElement.EnumerateObject().Count() != 6)
      throw new InvalidDataException("Self-check report shape is invalid.");
    var report = document.RootElement.Deserialize<SelfCheckReport>(StrictJson) ??
      throw new InvalidDataException("Self-check report is missing.");
    report.Validate(expectedVersion);
    return report;
  }

  private static void RejectDuplicates(JsonElement value)
  {
    if (value.ValueKind == JsonValueKind.Object)
    {
      var names = new HashSet<string>(StringComparer.Ordinal);
      foreach (var property in value.EnumerateObject())
      {
        if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate self-check report field.");
        RejectDuplicates(property.Value);
      }
    }
    else if (value.ValueKind == JsonValueKind.Array)
      foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
  }
}
