using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.FFmpeg.Tests.Tools;

public sealed class MediaToolLocatorTests
{
#pragma warning disable CA1707
  [Fact]
  public void Resolve_rejects_a_directory_without_both_executables()
  {
    using var directory = new TemporaryDirectory();
    var locator = new MediaToolLocator(directory.Path);

    var error = Assert.Throws<MediaToolException>(() => locator.Resolve());

    Assert.Contains("ffmpeg.exe", error.Message, StringComparison.Ordinal);
    Assert.Contains("ffprobe.exe", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Resolve_returns_absolute_paths_and_reported_versions()
  {
    using var directory = new TemporaryDirectory();
    directory.CreateFile("ffmpeg.exe");
    directory.CreateFile("ffprobe.exe");
    var locator = new MediaToolLocator(
      directory.Path,
      "ffmpeg version n9.0.1-11-ge47273f4d9",
      path => System.IO.Path.GetFileName(path) == "ffmpeg.exe"
        ? "ffmpeg version n9.0.1-11-ge47273f4d9 Copyright"
        : "ffprobe version n9.0.1-11-ge47273f4d9 Copyright");

    var tools = locator.Resolve();

    Assert.Equal(System.IO.Path.Combine(directory.Path, "ffmpeg.exe"), tools.FFmpegPath);
    Assert.Equal(System.IO.Path.Combine(directory.Path, "ffprobe.exe"), tools.FFprobePath);
    Assert.Equal("ffmpeg version n9.0.1-11-ge47273f4d9 Copyright", tools.FFmpegVersion);
    Assert.Equal("ffprobe version n9.0.1-11-ge47273f4d9 Copyright", tools.FFprobeVersion);
  }

  [Fact]
  public void Resolve_rejects_version_drift()
  {
    using var directory = new TemporaryDirectory();
    directory.CreateFile("ffmpeg.exe");
    directory.CreateFile("ffprobe.exe");
    var locator = new MediaToolLocator(
      directory.Path,
      "ffmpeg version n9.0.1-11-ge47273f4d9",
      path => System.IO.Path.GetFileName(path) == "ffmpeg.exe"
        ? "ffmpeg version n9.0.2"
        : "ffprobe version n9.0.2");

    var error = Assert.Throws<MediaToolException>(() => locator.Resolve());

    Assert.Contains("version drift", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("ffmpeg.exe", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Manifest_rejects_a_non_lowercase_sha256_digest()
  {
    using var directory = new TemporaryDirectory();
    var path = directory.CreateFile(
      "manifest.json",
      """
      {
        "schemaVersion": 1,
        "provider": "BtbN/FFmpeg-Builds",
        "release": "autobuild-2026-08-29-13-12",
        "asset": "ffmpeg.zip",
        "variant": "win64-lgpl",
        "expectedVersionPrefix": "ffmpeg version n9.0.1-11-ge47273f4d9",
        "sha256": "F43AAEB86D05B453F3909D0D1EED39A51DB71D387C21A3605676C1D1627084D9",
        "expectedExecutables": ["ffmpeg.exe", "ffprobe.exe"]
      }
      """);

    var error = Assert.Throws<MediaToolException>(() => MediaToolManifest.Load(path));

    Assert.Contains("64-character lowercase", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Manifest_rejects_a_null_sha256_digest_with_an_actionable_error()
  {
    using var directory = new TemporaryDirectory();
    var path = directory.CreateFile(
      "manifest.json",
      """
      {
        "schemaVersion": 1,
        "provider": "BtbN/FFmpeg-Builds",
        "release": "autobuild-2026-08-29-13-12",
        "asset": "ffmpeg.zip",
        "variant": "win64-lgpl",
        "expectedVersionPrefix": "ffmpeg version n9.0.1-11-ge47273f4d9",
        "sha256": null,
        "expectedExecutables": ["ffmpeg.exe", "ffprobe.exe"]
      }
      """);

    var error = Assert.Throws<MediaToolException>(() => MediaToolManifest.Load(path));

    Assert.Contains("64-character lowercase", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Acquisition_rejects_a_traversal_zip_without_writing_outside_or_publishing()
  {
    using var directory = new TemporaryDirectory();
    var escapedFileName = $"booksplice-escaped-{Guid.NewGuid():N}.txt";
    var escapedPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), escapedFileName);
    var archivePath = directory.GetPath("malicious.zip");
    var destinationPath = directory.GetPath("destination");
    CreateArchive(archivePath, $"../../{escapedFileName}", "must not escape");
    var manifestPath = CreateManifest(directory, archivePath);

    try
    {
      var result = RunAcquisition(manifestPath, destinationPath, archivePath);

      Assert.NotEqual(0, result.ExitCode);
      Assert.Contains("unsafe ZIP entry path", result.Output, StringComparison.OrdinalIgnoreCase);
      Assert.False(File.Exists(escapedPath));
      Assert.False(Directory.Exists(destinationPath));
    }
    finally
    {
      File.Delete(escapedPath);
    }
  }

  [Fact]
  public void Acquisition_rejects_a_zip_symlink_without_publishing()
  {
    using var directory = new TemporaryDirectory();
    var archivePath = directory.GetPath("symlink.zip");
    var destinationPath = directory.GetPath("destination");
    using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
    {
      var entry = archive.CreateEntry("ffmpeg.exe");
      entry.ExternalAttributes = unchecked((int)((0xA000u | 0x01FFu) << 16));
      using var writer = new StreamWriter(entry.Open());
      writer.Write("ffprobe.exe");
    }
    var manifestPath = CreateManifest(directory, archivePath);

    var result = RunAcquisition(manifestPath, destinationPath, archivePath);

    Assert.NotEqual(0, result.ExitCode);
    Assert.Contains("link or reparse", result.Output, StringComparison.OrdinalIgnoreCase);
    Assert.False(Directory.Exists(destinationPath));
  }
#pragma warning restore CA1707

  private static void CreateArchive(string path, string entryName, string contents)
  {
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    var entry = archive.CreateEntry(entryName);
    using var writer = new StreamWriter(entry.Open());
    writer.Write(contents);
  }

  private static string CreateManifest(TemporaryDirectory directory, string archivePath)
  {
    var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant();
    var manifest = new Dictionary<string, object>
    {
      ["schemaVersion"] = 1,
      ["provider"] = "BtbN/FFmpeg-Builds",
      ["release"] = "security-test-release",
      ["asset"] = System.IO.Path.GetFileName(archivePath),
      ["variant"] = "win64-lgpl",
      ["expectedVersionPrefix"] = "ffmpeg version n9.0.1-11-ge47273f4d9",
      ["sha256"] = digest,
      ["licenseSha256"] = new string('0', 64),
      ["expectedExecutables"] = new[] { "ffmpeg.exe", "ffprobe.exe" },
    };

    return directory.CreateFile("manifest.json", JsonSerializer.Serialize(manifest));
  }

  private static AcquisitionResult RunAcquisition(
    string manifestPath,
    string destinationPath,
    string archivePath)
  {
    var repositoryRoot = FindRepositoryRoot();
    var startInfo = new ProcessStartInfo
    {
      FileName = "pwsh",
      RedirectStandardError = true,
      RedirectStandardOutput = true,
      UseShellExecute = false,
      CreateNoWindow = true,
      WorkingDirectory = repositoryRoot,
    };
    startInfo.ArgumentList.Add("-NoProfile");
    startInfo.ArgumentList.Add("-File");
    startInfo.ArgumentList.Add(System.IO.Path.Combine(repositoryRoot, "scripts", "Get-MediaTools.ps1"));
    startInfo.ArgumentList.Add("-ManifestPath");
    startInfo.ArgumentList.Add(manifestPath);
    startInfo.ArgumentList.Add("-DestinationRoot");
    startInfo.ArgumentList.Add(destinationPath);
    startInfo.ArgumentList.Add("-ArchivePath");
    startInfo.ArgumentList.Add(archivePath);

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start pwsh.");
    var standardOutput = process.StandardOutput.ReadToEndAsync();
    var standardError = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(milliseconds: 30_000))
    {
      process.Kill(entireProcessTree: true);
      process.WaitForExit();
      throw new TimeoutException("Media tool acquisition test exceeded 30 seconds.");
    }

    return new AcquisitionResult(
      process.ExitCode,
      $"{standardOutput.GetAwaiter().GetResult()}\n{standardError.GetAwaiter().GetResult()}");
  }

  private static string FindRepositoryRoot()
  {
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
      if (File.Exists(System.IO.Path.Combine(directory.FullName, "scripts", "Get-MediaTools.ps1")))
      {
        return directory.FullName;
      }
    }

    throw new DirectoryNotFoundException("Unable to locate the repository root for the acquisition test.");
  }

  private sealed record AcquisitionResult(int ExitCode, string Output);

  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory()
    {
      Path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"booksplice-tests-{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string GetPath(string name) => System.IO.Path.Combine(Path, name);

    public string CreateFile(string name, string contents = "")
    {
      var path = System.IO.Path.Combine(Path, name);
      File.WriteAllText(path, contents);
      return path;
    }

    public void Dispose()
    {
      Directory.Delete(Path, recursive: true);
    }
  }
}
