using System.Text;
using BookSplice.Core.Analysis;
using BookSplice.Core.Metadata;

namespace BookSplice.Core.EditCopy;

public static class EditCopyPreview
{
  // Tags come from the probe; chapter titles from the edit's own chapter list, which keeps titles the probe's chpl read cuts short.
  public static IReadOnlyList<string> Describe(MediaProbeResult original, IReadOnlyList<EditCopyChapter> chapters, EditCopyRequest request)
  {
    ArgumentNullException.ThrowIfNull(original);
    ArgumentNullException.ThrowIfNull(chapters);
    ArgumentNullException.ThrowIfNull(request);
    var lines = new List<string>();
    foreach (var (field, value) in request.TagEdits.OrderBy(edit => edit.Key))
    {
      var before = OriginalValue(original, request.Profile, field);
      lines.Add($"{Label(field)}: {Quote(before)} -> {Quote(string.IsNullOrEmpty(value) ? null : value, "(removed)")}");
    }
    foreach (var (index, title) in request.ChapterTitleEdits.OrderBy(edit => edit.Key))
    {
      var before = index >= 0 && index < chapters.Count ? chapters[index].Title : null;
      lines.Add($"Chapter {index + 1}: {Quote(before)} -> {Quote(title)}");
    }
    return lines;
  }

  public static string Label(SemanticField field) => field switch
  {
    SemanticField.BookTitle => "Title",
    SemanticField.Asin => "ASIN",
    SemanticField.Isbn => "ISBN",
    SemanticField.AudioFileUrl => "Audio file URL",
    SemanticField.ItunesMediaType => "iTunes media type",
    SemanticField.ItunesGapless => "iTunes gapless",
    SemanticField.RatingWmp => "Rating (WMP)",
    SemanticField.AudibleAsin => "Audible ASIN",
    SemanticField.AudibleAlbumArtistId => "Audible album artist ID",
    SemanticField.AudibleAcr => "Audible ACR",
    _ => Words(field.ToString()),
  };

  // The file's current value for a field, read through the profile's tag keys. Null when the profile has no tag for it or the file has no value.
  public static string? OriginalValue(MediaProbeResult original, MetadataProfile? profile, SemanticField field)
  {
    ArgumentNullException.ThrowIfNull(original);
    if (profile is null) return null;
    foreach (var key in profile.Mappings.Where(mapping => mapping.Field == field).SelectMany(mapping => mapping.Keys))
    {
      if (original.FormatTags.TryGetValue(MetadataTagKeys.Canonical(key), out var value) && value.Length > 0) return value;
    }
    return null;
  }

  private static string Quote(string? value, string missing = "(none)") => value is null ? missing : $"\"{value}\"";

  private static string Words(string name)
  {
    var output = new StringBuilder(name.Length + 4);
    for (var index = 0; index < name.Length; index++)
    {
      var character = name[index];
      if (index > 0 && char.IsUpper(character)) output.Append(' ').Append(char.ToLowerInvariant(character));
      else output.Append(character);
    }
    return output.ToString();
  }
}
