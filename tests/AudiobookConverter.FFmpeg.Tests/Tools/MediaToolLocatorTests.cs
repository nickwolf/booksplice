using AudiobookConverter.FFmpeg.Tools;

namespace AudiobookConverter.FFmpeg.Tests.Tools;

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
#pragma warning restore CA1707

  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory()
    {
      Path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"audiobookconverter-tests-{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }

    public string Path { get; }

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
