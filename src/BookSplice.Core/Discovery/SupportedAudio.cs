namespace BookSplice.Core.Discovery;

public static class SupportedAudio
{
  private static readonly IReadOnlyList<string> ExtensionsValue = Array.AsReadOnly(new[] { ".mp3", ".aac", ".m4a", ".m4b", ".flac", ".ogg", ".opus", ".wma" });
  public static IReadOnlyList<string> Extensions => ExtensionsValue;

  public static bool IsSupported(string path)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    var name = Path.GetFileName(path);
    if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return false;
    return ExtensionsValue.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
  }
}
