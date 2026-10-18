using System.Text.Json;
using System.Runtime.Versioning;
using BookSplice.Updates.Policy;

namespace BookSplice.Updates.Storage;

[SupportedOSPlatform("windows")]
internal static class PackagePayloadValidator
{
  internal static void Validate(IReadOnlyList<PackageFile> files,
    Func<string, long, string> readText, string expectedVersion)
  {
    var byPath = files.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
    PackageFile Require(string path) => byPath.TryGetValue(path, out var file) ? file :
      throw new PackageException("Required package file is missing: " + path);
    void RequireHash(string path, string hash)
    {
      if (!string.Equals(Require(path).Sha256, hash, StringComparison.OrdinalIgnoreCase))
        throw new PackageException("Package checksum mismatch: " + path);
    }

    var required = new[]
    {
      "BookSplice.Gui.exe", "booksplice.exe", "BookSplice.Launcher.exe", "LICENSE", "README.md",
      "THIRD-PARTY-NOTICES.md", "VERSION.txt", "licenses/FFmpeg-LICENSE.txt",
      "licenses/FFmpeg-components.md", "licenses/LGPL-2.1.txt", "licenses/zlib-LICENSE.txt",
      "tools/ffmpeg/ffmpeg.exe", "tools/ffmpeg/ffprobe.exe", "sources/manifest.json",
      "sources/BUILDING.md", "sources/Dockerfile", "sources/build-minimal.sh",
      "sources/Build-MediaTools.ps1"
    };
    foreach (var path in required) Require(path);
    if (files.Any(file => file.RelativePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
      throw new PackageException("The package contains debug symbols.");
    if (readText("README.md", UpdateLimits.ManifestMaxBytes).Contains("{{VERSION}}", StringComparison.Ordinal))
      throw new PackageException("The release README has an unresolved version placeholder.");

    var version = readText("VERSION.txt", 4096);
    var lines = version.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
    if (lines.Length != 4 || lines[0] != "BookSplice " + expectedVersion ||
        lines[1] != "Runtime: Windows x64, self-contained .NET 10" ||
        !lines[3].StartsWith("Media license SHA-256: ", StringComparison.Ordinal))
      throw new PackageException("VERSION.txt does not match the expected release.");
    var versionLicense = lines[3]["Media license SHA-256: ".Length..];
    if (!IsHash(versionLicense)) throw new PackageException("VERSION.txt has an invalid license hash.");

    using var json = ParseSourceManifest(readText("sources/manifest.json", UpdateLimits.ManifestMaxBytes));
    var root = json.RootElement;
    if (RequiredInt(root, "schemaVersion") != 2) throw new PackageException("Unsupported source manifest schema.");
    var mediaRelease = RequiredString(root, "release");
    if (mediaRelease.Length is < 1 or > 128 || mediaRelease.Any(character =>
        !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_')) ||
        lines[2] != "Media tools: source-built FFmpeg " + mediaRelease + ", LGPL 2.1 or later; zlib 1.3.2")
      throw new PackageException("VERSION.txt media release does not match the source manifest.");
    var license = RequiredHash(root, "licenseSha256");
    RequireHash("licenses/FFmpeg-LICENSE.txt", license);
    if (!string.Equals(versionLicense, license, StringComparison.OrdinalIgnoreCase))
      throw new PackageException("VERSION.txt and source manifest license hashes differ.");
    RequireHash("licenses/zlib-LICENSE.txt", RequiredHash(root, "zlibLicenseSha256"));
    RequireHash("tools/ffmpeg/ffmpeg.exe", RequiredHash(root, "ffmpegSha256"));
    RequireHash("tools/ffmpeg/ffprobe.exe", RequiredHash(root, "ffprobeSha256"));
    var sources = Required(root, "sourceArchives");
    if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() is < 1 or > 100)
      throw new PackageException("Invalid source archive list.");
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
      "manifest.json", "BUILDING.md", "Dockerfile", "build-minimal.sh", "Build-MediaTools.ps1"
    };
    foreach (var source in sources.EnumerateArray())
    {
      if (source.ValueKind != JsonValueKind.Object) throw new PackageException("Invalid source archive entry.");
      var name = RequiredString(source, "fileName");
      if (!SafeZipExtractor.IsSafeComponent(name) || !names.Add(name))
        throw new PackageException("Invalid or duplicate source archive name.");
      RequireHash("sources/" + name, RequiredHash(source, "sha256"));
    }
  }

  private static JsonDocument ParseSourceManifest(string text)
  {
    try
    {
      var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
      CheckDuplicateProperties(document.RootElement);
      if (document.RootElement.ValueKind != JsonValueKind.Object)
        throw new PackageException("Invalid source manifest.");
      return document;
    }
    catch (JsonException error) { throw new PackageException("Invalid source manifest JSON.", error); }
  }

  private static void CheckDuplicateProperties(JsonElement element)
  {
    if (element.ValueKind == JsonValueKind.Object)
    {
      var names = new HashSet<string>(StringComparer.Ordinal);
      foreach (var property in element.EnumerateObject())
      {
        if (!names.Add(property.Name)) throw new PackageException("Duplicate source manifest property.");
        CheckDuplicateProperties(property.Value);
      }
    }
    else if (element.ValueKind == JsonValueKind.Array)
      foreach (var item in element.EnumerateArray()) CheckDuplicateProperties(item);
  }

  private static JsonElement Required(JsonElement parent, string name) =>
    parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value :
    throw new PackageException("Missing source manifest property: " + name);

  private static string RequiredString(JsonElement parent, string name)
  {
    var value = Required(parent, name);
    return value.ValueKind == JsonValueKind.String && value.GetString() is { } text ? text :
      throw new PackageException("Invalid source manifest string: " + name);
  }

  private static int RequiredInt(JsonElement parent, string name)
  {
    var value = Required(parent, name);
    return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number :
      throw new PackageException("Invalid source manifest number: " + name);
  }

  private static string RequiredHash(JsonElement parent, string name)
  {
    var value = RequiredString(parent, name);
    return IsHash(value) ? value : throw new PackageException("Invalid source manifest hash: " + name);
  }

  private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}
