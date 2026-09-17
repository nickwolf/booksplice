using System.Text;
using BookSplice.Core.Discovery;

namespace BookSplice.Core.Metadata;

public interface IMetadataAggregator
{
  BookMetadata Aggregate(IReadOnlyList<SourceFile> files);
}

public sealed class MetadataAggregator : IMetadataAggregator
{
  private static readonly IReadOnlyDictionary<SemanticField, IReadOnlyList<string>> Keys = new Dictionary<SemanticField, IReadOnlyList<string>>
  {
    [SemanticField.Author] = ["ALBUMARTIST", "album_artist", "ARTIST", "artist"],
    [SemanticField.Narrator] = ["COMPOSER", "composer"],
    [SemanticField.Subtitle] = ["SUBTITLE", "subtitle"],
    [SemanticField.AlbumSort] = ["ALBUMSORT", "sort_album"],
    [SemanticField.Series] = ["SERIES", "series"],
    [SemanticField.SeriesPart] = ["SERIES-PART", "series-part", "SERIESPART"],
    [SemanticField.Genre] = ["GENRE", "genre"],
    [SemanticField.Year] = ["YEAR", "year", "DATE", "date"],
    [SemanticField.ReleaseTime] = ["RELEASETIME", "releasetime"],
    [SemanticField.Comment] = ["COMMENT", "comment"],
    [SemanticField.Description] = ["DESCRIPTION", "description"],
    [SemanticField.Publisher] = ["PUBLISHER", "publisher"],
    [SemanticField.Copyright] = ["COPYRIGHT", "copyright"],
    [SemanticField.Asin] = ["ASIN", "asin"],
    [SemanticField.AudioFileUrl] = ["WWWAUDIOFILE", "wwwaudiofile"],
    [SemanticField.Isbn] = ["ISBN", "isbn"],
    [SemanticField.Language] = ["LANGUAGE", "language"],
    [SemanticField.ItunesMediaType] = ["ITUNESMEDIATYPE", "media_type"],
    [SemanticField.RatingWmp] = ["RATING WMP", "rating wmp"],
    [SemanticField.ContentGroup] = ["CONTENTGROUP", "grouping"],
    [SemanticField.MovementName] = ["MOVEMENTNAME", "movementname"],
    [SemanticField.Movement] = ["MOVEMENT", "movement"],
    [SemanticField.ItunesGapless] = ["ITUNESGAPLESS", "gapless_playback"],
    [SemanticField.AudibleAsin] = ["AUDIBLE_ASIN", "audible_asin"],
    [SemanticField.AudibleAlbumArtistId] = ["AUDIBLE_ALBUMARTISTID", "audible_albumartistid"],
    [SemanticField.AudibleAcr] = ["AUDIBLE_ACR", "audible_acr"],
    [SemanticField.AudibleLocale] = ["AUDIBLE_LOCALE", "audible_locale"],
    [SemanticField.Format] = ["FORMAT", "format"],
    [SemanticField.Explicit] = ["EXPLICIT", "explicit"],
    [SemanticField.Rating] = ["RATING", "rating"],
    [SemanticField.Track] = ["TRACK", "TRACKNUMBER", "track", "tracknumber"],
    [SemanticField.Disc] = ["DISC", "DISCNUMBER", "disc", "discnumber"]
  };

  public BookMetadata Aggregate(IReadOnlyList<SourceFile> files)
  {
    ArgumentNullException.ThrowIfNull(files);
    var fields = new Dictionary<SemanticField, AggregatedValue>();
    foreach (var field in Keys.Keys.Where(field => field is not SemanticField.Track and not SemanticField.Disc))
    {
      fields[field] = AggregateField(field, files);
    }

    fields[SemanticField.BookTitle] = AggregateBookTitle(files);
    return new BookMetadata(fields, PreserveTags(files), Keys);
  }

  private static AggregatedValue AggregateBookTitle(IReadOnlyList<SourceFile> files)
  {
    var album = AggregateKeys(SemanticField.BookTitle, files, ["ALBUM", "album"]);
    if (album.State is AggregationState.Consistent or AggregationState.PartiallyMissing) return album;
    var title = AggregateKeys(SemanticField.BookTitle, files, ["TITLE", "title"]);
    if (album.State == AggregationState.Conflicting)
    {
      if (title.State == AggregationState.Consistent) return WithProvisionalValue(album, title.Value, title.Candidates);
      var root = SourceRoot(files);
      return root is null ? album : WithProvisionalValue(album, root, [new MetadataCandidate(root, root, "source-root")]);
    }
    if (title.State == AggregationState.Consistent) return title;
    var sourceRoot = SourceRoot(files);
    return sourceRoot is null ? AggregatedValue.Missing(SemanticField.BookTitle) : new AggregatedValue(SemanticField.BookTitle, AggregationState.Consistent, sourceRoot, [new MetadataCandidate(sourceRoot, sourceRoot, "source-root")]);
  }

  private static AggregatedValue WithProvisionalValue(AggregatedValue evidence, string? value, IReadOnlyList<MetadataCandidate> provisionalCandidates)
    => new(evidence.Field, evidence.State, value, evidence.Candidates.Concat(provisionalCandidates).ToArray());
  private static AggregatedValue AggregateField(SemanticField field, IReadOnlyList<SourceFile> files)
  {
    if (field == SemanticField.Author)
    {
      var albumArtist = AggregateKeys(field, files, ["ALBUMARTIST", "album_artist"]);
      return albumArtist.State == AggregationState.Missing ? AggregateKeys(field, files, ["ARTIST", "artist"]) : albumArtist;
    }

    if (field == SemanticField.SeriesPart) return AggregateKeys(field, files, ["SERIES-PART", "series-part"]);
    return AggregateKeys(field, files, Keys[field]);
  }

  private static AggregatedValue AggregateKeys(SemanticField field, IReadOnlyList<SourceFile> files, IReadOnlyList<string> keys)
  {
    var candidates = new List<MetadataCandidate>();
    foreach (var file in files)
    {
      var tags = Tags(file);
      var match = keys.Select(key => tags.FirstOrDefault(tag => string.Equals(tag.Key, key, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(tag => !string.IsNullOrWhiteSpace(tag.Value));
      if (!string.IsNullOrWhiteSpace(match.Value)) candidates.Add(new MetadataCandidate(match.Value, file.RelativePath, match.Key));
    }

    if (candidates.Count == 0) return AggregatedValue.Missing(field);
    var normalizedValues = candidates.Select(candidate => NormalizeForComparison(candidate.Value)).Distinct(StringComparer.Ordinal).ToArray();
    var state = normalizedValues.Length > 1
      ? AggregationState.Conflicting
      : candidates.Count == files.Count ? AggregationState.Consistent : AggregationState.PartiallyMissing;
    return new AggregatedValue(field, state, candidates[0].Value, candidates);
  }

  private static Dictionary<string, string> PreserveTags(IEnumerable<SourceFile> files)
  {
    var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var file in files)
    {
      foreach (var tag in Tags(file))
      {
        if (!output.ContainsKey(tag.Key)) output.Add(tag.Key, tag.Value);
      }
    }

    return output;
  }

  private static IReadOnlyDictionary<string, string> Tags(SourceFile file)
    => file.ProbeResult.RawTags.Count > 0 ? file.ProbeResult.RawTags : file.ProbeResult.FormatTags;

  private static string NormalizeForComparison(string value)
  {
    var normalized = value.Normalize(NormalizationForm.FormC);
    var builder = new StringBuilder(normalized.Length);
    var pendingSpace = false;
    foreach (var character in normalized)
    {
      if (char.IsWhiteSpace(character))
      {
        pendingSpace = builder.Length > 0;
      }
      else
      {
        if (pendingSpace) builder.Append(' ');
        builder.Append(character);
        pendingSpace = false;
      }
    }

    return builder.ToString();
  }

  private static string? SourceRoot(IReadOnlyList<SourceFile> files)
  {
    var directories = files.Select(file => Path.GetDirectoryName(file.FullPath)).Where(directory => !string.IsNullOrWhiteSpace(directory)).Cast<string>().ToArray();
    if (directories.Length == 0) return null;
    var root = directories[0];
    foreach (var directory in directories.Skip(1))
    {
      while (!IsSameOrDescendant(directory, root))
      {
        root = Path.GetDirectoryName(root);
        if (string.IsNullOrWhiteSpace(root)) return null;
      }
    }

    return Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
  }

  private static bool IsSameOrDescendant(string path, string parent)
    => string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
