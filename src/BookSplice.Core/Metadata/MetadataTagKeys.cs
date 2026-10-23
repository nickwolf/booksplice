namespace BookSplice.Core.Metadata;

public static class MetadataTagKeys
{
  public static string Canonical(string key) => key.Equals("ALBUMARTIST", StringComparison.OrdinalIgnoreCase) ? "album_artist" : key;
}
