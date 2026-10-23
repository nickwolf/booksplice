using System.Globalization;

namespace BookSplice.Core.Planning;

public static class SpaceEstimateDescription
{
  public static string FormatBytes(long bytes)
  {
    string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
    var value = (double)Math.Max(bytes, 0);
    var unit = 0;
    while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
    return unit == 0 ? $"{(long)value} bytes" : $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
  }

  public static IReadOnlyList<string> Lines(SpaceEstimate space)
  {
    ArgumentNullException.ThrowIfNull(space);
    if (space.TemporaryVolume is null)
      return [$"Estimated space needed: {FormatBytes(space.TotalRequiredBytes)}{OnVolume(space.DestinationVolume)} for output and temporary files with a 10% margin ({Free(space.AvailableBytes)})"];
    return
    [
      $"Estimated output size: {FormatBytes(space.FinalBytes)}{OnVolume(space.DestinationVolume)} ({Free(space.AvailableBytes)})",
      $"Estimated temporary space: {FormatBytes(space.TemporaryBytes)}{OnVolume(space.TemporaryVolume)} ({Free(space.TemporaryAvailableBytes)})",
    ];
  }

  public static string VolumeLabel(string? root)
  {
    if (string.IsNullOrWhiteSpace(root)) return string.Empty;
    var trimmed = root.TrimEnd('\\', '/');
    return trimmed.Length > 0 ? trimmed : root;
  }

  private static string OnVolume(string? root) => VolumeLabel(root) is { Length: > 0 } label ? $" on {label}" : string.Empty;
  private static string Free(long? available) => available is { } value ? $"free space: {FormatBytes(value)}" : "free space unavailable";
}
