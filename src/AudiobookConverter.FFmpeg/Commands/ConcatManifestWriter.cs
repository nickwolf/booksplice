using System.Text;

namespace AudiobookConverter.FFmpeg.Commands;

public sealed class ConcatManifestWriter
{
  public static string Write(IEnumerable<string> sourcePaths)
  {
    ArgumentNullException.ThrowIfNull(sourcePaths);
    var entries = sourcePaths.Select(Path.GetFullPath).ToArray();
    if (entries.Length == 0) throw new ArgumentException("At least one source path is required.", nameof(sourcePaths));
    foreach (var path in entries) Validate(path);
    return string.Concat(entries.Select(path => $"file '{path.Replace('\\', '/').Replace("'", "\\'")}'\n"));
  }
  public static void WriteFile(string path, IEnumerable<string> sourcePaths)
  {
    Validate(path); File.WriteAllText(path, Write(sourcePaths), new UTF8Encoding(false));
  }
  internal static void Validate(string path)
  {
    if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new ArgumentException("The path is unsafe for FFmpeg.", nameof(path));
  }
}
