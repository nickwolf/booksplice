using System.Text;
using System.Text.Json;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Handover;

public sealed record CandidateStartupRequest(string PipeName, Guid TransactionId, string Nonce,
  ProcessIdentity Coordinator, FileIdentity CoordinatorImage, string CoordinatorSha256,
  ProcessIdentity OldGui, FileIdentity OldGuiImage, string OldGuiSha256,
  string DataRoot, FileIdentity DataRootIdentity, string ExpectedSettingsSha256, string Version)
{
  private static readonly JsonSerializerOptions StrictJson = new()
  {
    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true
  };

  public string Encode()
  {
    Validate();
    var bytes = JsonSerializer.SerializeToUtf8Bytes(this);
    if (bytes.Length > 6144) throw new InvalidDataException("Candidate context size is invalid.");
    return Convert.ToBase64String(bytes);
  }

  public static CandidateStartupRequest Parse(string encoded,
    Func<string, bool>? packageMatches = null)
  {
    if (encoded.Length is 0 or > 8192) throw new InvalidDataException("Candidate context size is invalid.");
    var bytes = Convert.FromBase64String(encoded);
    if (bytes.Length > 6144) throw new InvalidDataException("Candidate context size is invalid.");
    _ = new UTF8Encoding(false, true).GetString(bytes);
    using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
    if (document.RootElement.ValueKind != JsonValueKind.Object ||
        document.RootElement.EnumerateObject().Count() != 13 ||
        document.RootElement.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 13)
      throw new InvalidDataException("Candidate context shape is invalid.");
    var context = document.RootElement.Deserialize<CandidateStartupRequest>(StrictJson) ??
      throw new InvalidDataException("Candidate context is missing.");
    context.Validate();
    if (packageMatches is not null && !packageMatches(context.Version))
      throw new InvalidDataException("Candidate package identity differs from the release.");
    return context;
  }

  private void Validate()
  {
    if (TransactionId == Guid.Empty || !ValidPipeName(PipeName) ||
        Coordinator is null || OldGui is null ||
        Coordinator.Pid <= 0 || Coordinator.StartTimeUtcTicks <= 0 ||
        OldGui.Pid <= 0 || OldGui.StartTimeUtcTicks <= 0 ||
        !string.Equals(Path.GetFileName(Coordinator.ExecutablePath),
          "BookSplice.Launcher.exe", StringComparison.OrdinalIgnoreCase) ||
        DataRoot is null || !Path.IsPathFullyQualified(DataRoot) ||
        Path.GetFullPath(DataRoot) != DataRoot ||
        !string.Equals(Path.GetFileName(DataRoot), "BookSplice", StringComparison.OrdinalIgnoreCase) ||
        !DataRootIdentity.IsValid || !CoordinatorImage.IsValid || !OldGuiImage.IsValid ||
        !ValidHash(CoordinatorSha256) || !ValidHash(OldGuiSha256) ||
        ExpectedSettingsSha256 != "absent" && !ValidHash(ExpectedSettingsSha256) ||
        Nonce is not { Length: 44 } || Convert.FromBase64String(Nonce).Length != 32 ||
        string.IsNullOrWhiteSpace(Version) || Version.Length > 128)
      throw new InvalidDataException("Candidate context identity is invalid.");
  }

  private static bool ValidHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
  private static bool ValidPipeName(string name) => name is { Length: 66 } &&
    name.StartsWith("booksplice-update-", StringComparison.Ordinal) &&
    name[18..].All(Uri.IsHexDigit);
}
