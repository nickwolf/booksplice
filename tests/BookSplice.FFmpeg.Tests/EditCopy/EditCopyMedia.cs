using System.Buffers.Binary;
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

  private static readonly string[] FaststartArguments = ["-movflags", "+faststart"];

  public static MediaToolSet Tools() => new MediaToolLocator(Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR")!).Resolve();

  public static async Task<string> GenerateAsync(string folder, MediaToolSet tools, IReadOnlyList<string> chapterTitles, int seconds = 6, bool cover = true, string name = "book.m4b", bool faststart = true, Action<string>? beforeTags = null, IReadOnlyDictionary<string, string>? extraTags = null, bool plainVideo = false)
  {
    var output = Path.Combine(folder, name);
    var metadata = Path.Combine(folder, $"{name}.ffmeta");
    var text = new StringBuilder(";FFMETADATA1\n");
    foreach (var (key, value) in NativeTags.Concat(extraTags ?? new Dictionary<string, string>())) text.Append(CultureInfo.InvariantCulture, $"{key}={FFmetadataWriter.Escape(value)}\n");
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
    cover &= !plainVideo;
    if (cover)
    {
      var image = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=64x64:d=1", "-frames:v", "1", "-c:v", "mjpeg", coverPath]), null, CancellationToken.None);
      Assert.Equal(0, image.ExitCode);
    }
    // A plain video stream (not a cover) stands in for chapter images; the ipod muxer won't take it, so that file is plain mp4.
    string[] coverArgs = plainVideo ? ["-f", "lavfi", "-i", $"color=c=red:s=32x32:r=1:d={seconds}", "-map", "2:v:0", "-c:v", "mjpeg"]
      : cover ? ["-i", coverPath, "-map", "2:v:0", "-c:v", "copy", "-disposition:v:0", "attached_pic"] : [];
    var generated = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath,
    [
      "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}", "-f", "ffmetadata", "-i", metadata, .. coverArgs,
      "-map", "0:a:0", "-map_metadata", "1", "-map_chapters", "1", "-c:a", "aac", "-ar", "44100", "-ac", "1", .. faststart ? FaststartArguments : [], "-f", plainVideo ? "mp4" : "ipod", output,
    ]), null, CancellationToken.None);
    Assert.True(generated.ExitCode == 0, generated.StandardError);
    beforeTags?.Invoke(output);
    new Mp4MetadataWriter().Write(output, FreeformTags);
    File.Delete(metadata);
    if (cover) File.Delete(coverPath);
    return output;
  }

  // A box shaped like Windows Explorer's Xtra: one entry naming a property, its type and its value.
  public static byte[] XtraBox() => Box("Xtra", [0, 0, 0, 0x27, 0, 0, 0, 0x13], Encoding.ASCII.GetBytes("WM/SharedUserRating"), [0, 0, 0, 1, 0, 0, 0, 0x0E, 0, 0x13, 0, 0, 0, 0, 0, 0, 0, 0x63]);

  // A moov-level QuickTime metadata box: hdlr mdta, one key, one UTF-8 value in ilst.
  public static byte[] MdtaMetaBox(string key, string value)
    => Box("meta", [0, 0, 0, 0],
      Box("hdlr", [0, 0, 0, 0, 0, 0, 0, 0], "mdta"u8.ToArray(), new byte[12], [0]),
      Box("keys", [0, 0, 0, 0, 0, 0, 0, 1], Box("mdta", Encoding.UTF8.GetBytes(key))),
      Box("ilst", Box([0, 0, 0, 1], Box("data", [0, 0, 0, 1, 0, 0, 0, 0], Encoding.UTF8.GetBytes(value)))));

  // Appends a child to moov/udta and a box to moov. The file must end with moov, so no media offsets move.
  public static void InjectUserData(string path, byte[] udtaChild, byte[] moovChild)
  {
    var file = File.ReadAllBytes(path);
    var moov = TopLevel(file).Single(box => box.Type == "moov");
    Assert.Equal(file.Length, moov.Offset + moov.Size);
    var children = new MemoryStream();
    var sawUdta = false;
    for (var offset = moov.Offset + 8; offset < moov.Offset + moov.Size;)
    {
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset));
      var raw = file[offset..(offset + size)];
      if (Encoding.Latin1.GetString(raw, 4, 4) == "udta") { raw = Box("udta", raw[8..], udtaChild); sawUdta = true; }
      children.Write(raw);
      offset += size;
    }
    Assert.True(sawUdta);
    children.Write(moovChild);
    File.WriteAllBytes(path, [.. file[..moov.Offset], .. Box("moov", children.ToArray())]);
  }

  public static string LastTopLevelType(string path) => TopLevel(File.ReadAllBytes(path)).Last().Type;

  private static IEnumerable<(string Type, int Offset, int Size)> TopLevel(byte[] file)
  {
    for (var offset = 0; offset + 8 <= file.Length;)
    {
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset));
      Assert.InRange(size, 8, file.Length - offset);
      yield return (Encoding.Latin1.GetString(file, offset + 4, 4), offset, size);
      offset += size;
    }
  }

  public static byte[] Box(string type, params byte[][] parts) => Box(Encoding.Latin1.GetBytes(type), parts);

  public static byte[] Box(byte[] type, params byte[][] parts)
  {
    var payload = parts.SelectMany(part => part).ToArray();
    var box = new byte[payload.Length + 8];
    BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
    type.CopyTo(box, 4);
    payload.CopyTo(box, 8);
    return box;
  }

  // A book with moov at the end, an Xtra box after chpl in udta, and a moov-level mdta meta box. The conversion writer's
  // TagLib save scrambles the Xtra bytes (it only puts chpl back), so the intended bytes are written over it afterwards.
  public static async Task<string> GenerateWithUserDataAsync(string folder, MediaToolSet tools, int chapters = 3)
  {
    var xtra = XtraBox();
    var path = await GenerateAsync(folder, tools, Titles(chapters), faststart: false, beforeTags: path => InjectUserData(path, xtra, MdtaMetaBox("com.apple.quicktime.title", "Quick title")));
    var written = Assert.Single(Mp4MetadataBoxes.ReadUserData(path).UdtaChildren, box => box.Type == "Xtra").Raw;
    Assert.Equal(xtra.Length, written.Length);
    var file = await File.ReadAllBytesAsync(path);
    var offset = file.AsSpan().IndexOf(written);
    Assert.True(offset > 0);
    xtra.CopyTo(file, offset);
    await File.WriteAllBytesAsync(path, file);
    Assert.Equal(Convert.ToHexString(xtra), Convert.ToHexString(Assert.Single(Mp4MetadataBoxes.ReadUserData(path).UdtaChildren, box => box.Type == "Xtra").Raw));
    return path;
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
