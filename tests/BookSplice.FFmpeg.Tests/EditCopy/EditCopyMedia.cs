using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BookSplice.FFmpeg.Commands;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.FFmpeg.Tests.EditCopy;

// Generates books shaped like BookSplice's own output: ipod muxer with faststart, a cover, chapters, and freeform tags written by Mp4MetadataWriter.
internal static class EditCopyMedia
{
  public static readonly IReadOnlyDictionary<string, string> NativeTags = new Dictionary<string, string>
  {
    ["title"] = "Test Book",
    ["album"] = "Test Book",
    ["artist"] = "Test Author",
    ["album_artist"] = "Test Author",
    ["composer"] = "Test Narrator",
    ["genre"] = "Fantasy",
    ["comment"] = "Native comment",
    ["media_type"] = "2",
  };

  public static readonly IReadOnlyDictionary<string, string> FreeformTags = new Dictionary<string, string>
  {
    ["comment"] = "Freeform comment",
    ["language"] = "eng",
    ["BOOKSPLICE_NOTE"] = "Keep this one",
  };

  public static MediaToolSet Tools() => new MediaToolLocator(Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR")!).Resolve();

  public static async Task<string> GenerateAsync(string folder, MediaToolSet tools, IReadOnlyList<string> chapterTitles, int seconds = 6, bool cover = true, string name = "book.m4b")
  {
    var output = Path.Combine(folder, name);
    var metadata = Path.Combine(folder, $"{name}.ffmeta");
    var text = new StringBuilder(";FFMETADATA1\n");
    foreach (var (key, value) in NativeTags) text.Append(CultureInfo.InvariantCulture, $"{key}={FFmetadataWriter.Escape(value)}\n");
    var length = seconds * 1000L;
    for (var index = 0; index < chapterTitles.Count; index++)
    {
      var start = length * index / chapterTitles.Count;
      var end = length * (index + 1) / chapterTitles.Count;
      text.Append(CultureInfo.InvariantCulture, $"[CHAPTER]\nTIMEBASE=1/1000\nSTART={start}\nEND={end}\ntitle={FFmetadataWriter.Escape(chapterTitles[index])}\n");
    }
    await File.WriteAllTextAsync(metadata, text.ToString(), new UTF8Encoding(false));
    var runner = new ProcessRunner();
    var coverPath = Path.Combine(folder, $"{name}.jpg");
    if (cover)
    {
      var image = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=64x64:d=1", "-frames:v", "1", "-c:v", "mjpeg", coverPath]), null, CancellationToken.None);
      Assert.Equal(0, image.ExitCode);
    }
    string[] coverArgs = cover ? ["-i", coverPath, "-map", "2:v:0", "-c:v", "copy", "-disposition:v:0", "attached_pic"] : [];
    var generated = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath,
    [
      "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}", "-f", "ffmetadata", "-i", metadata, .. coverArgs,
      "-map", "0:a:0", "-map_metadata", "1", "-map_chapters", "1", "-c:a", "aac", "-ar", "44100", "-ac", "1", "-movflags", "+faststart", "-f", "ipod", output,
    ]), null, CancellationToken.None);
    Assert.True(generated.ExitCode == 0, generated.StandardError);
    new Mp4MetadataWriter().Write(output, FreeformTags);
    File.Delete(metadata);
    if (cover) File.Delete(coverPath);
    return output;
  }

  public static string[] Titles(int count, Func<int, string>? title = null) => Enumerable.Range(0, count).Select(index => title?.Invoke(index) ?? $"Chapter {index + 1}").ToArray();

  public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

  public static IReadOnlyList<string> Files(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories) : [];
}

internal sealed class TemporaryFolder : IDisposable
{
  public TemporaryFolder() => Path = Directory.CreateTempSubdirectory("booksplice-edit-copy-").FullName;
  public string Path { get; }
  public string GetPath(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
  public string CreateFolder(params string[] parts) => Directory.CreateDirectory(GetPath(parts)).FullName;
  public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
