using System.Security.Cryptography;
using System.Text.Json;

namespace BookSplice.Updates.Tests.Storage;

internal static class OriginalPackageFixture
{
  internal static void Write(string root, string version = "1.0.0")
  {
    Directory.CreateDirectory(root);
    foreach (var path in new[]
    {
      "BookSplice.Gui.exe", "booksplice.exe", "BookSplice.Launcher.exe", "LICENSE",
      "README.md", "THIRD-PARTY-NOTICES.md", "licenses/FFmpeg-LICENSE.txt",
      "licenses/FFmpeg-components.md", "licenses/LGPL-2.1.txt", "licenses/zlib-LICENSE.txt",
      "tools/ffmpeg/ffmpeg.exe", "tools/ffmpeg/ffprobe.exe", "sources/BUILDING.md",
      "sources/Dockerfile", "sources/build-minimal.sh", "sources/Build-MediaTools.ps1",
      "sources/source.tar.gz"
    })
    {
      var destination = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
      File.WriteAllText(destination, path);
    }
    string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(
      Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))));
    var licenseHash = Hash("licenses/FFmpeg-LICENSE.txt");
    File.WriteAllText(Path.Combine(root, "VERSION.txt"),
      $"BookSplice {version}\nRuntime: Windows x64, self-contained .NET 10\n" +
      $"Media tools: source-built FFmpeg 7.0, LGPL 2.1 or later; zlib 1.3.2\n" +
      $"Media license SHA-256: {licenseHash}\n");
    var manifest = new
    {
      schemaVersion = 2,
      release = "7.0",
      licenseSha256 = licenseHash,
      zlibLicenseSha256 = Hash("licenses/zlib-LICENSE.txt"),
      ffmpegSha256 = Hash("tools/ffmpeg/ffmpeg.exe"),
      ffprobeSha256 = Hash("tools/ffmpeg/ffprobe.exe"),
      sourceArchives = new[] { new { fileName = "source.tar.gz", sha256 = Hash("sources/source.tar.gz") } }
    };
    File.WriteAllText(Path.Combine(root, "sources", "manifest.json"),
      JsonSerializer.Serialize(manifest));
  }
}
