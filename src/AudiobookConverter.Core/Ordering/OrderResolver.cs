using System.Globalization;
using AudiobookConverter.Core.Discovery;

namespace AudiobookConverter.Core.Ordering;

public interface IOrderResolver { OrderResolution Resolve(IReadOnlyList<SourceFile> files); }

public sealed class OrderResolver : IOrderResolver
{
  private static readonly string[] TrackKeys = ["TRACKNUMBER", "TRACK", "TRCK"];
  private static readonly string[] DiscKeys = ["DISCNUMBER", "DISC", "DISKNUMBER", "DISK"];
  private static readonly NaturalPathComparer NaturalPaths = new();

  public OrderResolution Resolve(IReadOnlyList<SourceFile> files)
  {
    ArgumentNullException.ThrowIfNull(files);
    var naturalFiles = files.OrderBy(file => file.RelativePath, NaturalPaths).ToArray();
    var natural = new OrderCandidate(OrderCandidateId.NaturalPath, naturalFiles.Select(FileId), true, OrderConfidence.Medium, []);
    var metadata = CreateMetadataCandidate(files, naturalFiles);
    var candidates = new[] { natural, metadata };
    var evidence = candidates.SelectMany(candidate => candidate.Evidence).ToArray();
    if (metadata.IsCredible && SameOrder(natural, metadata))
    {
      var selected = new OrderCandidate(OrderCandidateId.Metadata, metadata.FileIds, true, OrderConfidence.High, metadata.Evidence);
      candidates = [new OrderCandidate(OrderCandidateId.NaturalPath, natural.FileIds, true, OrderConfidence.High, natural.Evidence), selected];
      return new OrderResolution(OrderStatus.Resolved, selected, candidates, evidence);
    }
    if (metadata.IsCredible) return new OrderResolution(OrderStatus.NeedsDecision, null, candidates, evidence);
    return new OrderResolution(OrderStatus.Resolved, natural, candidates, evidence);
  }

  private static OrderCandidate CreateMetadataCandidate(IReadOnlyList<SourceFile> files, IReadOnlyList<SourceFile> naturalFiles)
  {
    var entries = new List<MetadataEntry>(files.Count);
    var evidence = new List<OrderEvidence>();
    foreach (var file in files)
    {
      var track = ParsePositiveTag(file, TrackKeys);
      var disc = ParsePositiveTag(file, DiscKeys);
      if (!track.IsPresent) evidence.Add(Evidence("ordering.metadata.missing-track", file));
      else if (!track.IsValid) evidence.Add(Evidence("ordering.metadata.invalid-track", file));
      entries.Add(new MetadataEntry(file, track, disc));
    }
    var allTracksValid = entries.All(entry => entry.Track.IsValid);
    var anyDiscPresent = entries.Any(entry => entry.Disc.IsPresent);
    var allDiscsValid = entries.All(entry => entry.Disc.IsValid);
    if (anyDiscPresent && !allDiscsValid)
    {
      foreach (var entry in entries.Where(entry => !entry.Disc.IsPresent)) evidence.Add(Evidence("ordering.metadata.missing-disc", entry.File));
      foreach (var entry in entries.Where(entry => entry.Disc.IsPresent && !entry.Disc.IsValid)) evidence.Add(Evidence("ordering.metadata.invalid-disc", entry.File));
    }
    var uniqueKeys = false;
    if (allTracksValid && (!anyDiscPresent || allDiscsValid))
    {
      uniqueKeys = anyDiscPresent
        ? entries.Select(entry => (entry.Disc.Value, entry.Track.Value)).Distinct().Count() == entries.Count
        : entries.Select(entry => entry.Track.Value).Distinct().Count() == entries.Count;
      if (!uniqueKeys)
      {
        var duplicates = anyDiscPresent
          ? entries.GroupBy(entry => (entry.Disc.Value, entry.Track.Value)).Where(group => group.Count() > 1).Select(group => group.Select(entry => FileId(entry.File)))
          : entries.GroupBy(entry => entry.Track.Value).Where(group => group.Count() > 1).Select(group => group.Select(entry => FileId(entry.File)));
        foreach (var duplicate in duplicates) evidence.Add(new OrderEvidence(anyDiscPresent ? "ordering.metadata.duplicate-disc-track" : "ordering.metadata.duplicate-track", Array.AsReadOnly(duplicate.ToArray())));
      }
    }
    var credible = allTracksValid && (!anyDiscPresent || allDiscsValid) && uniqueKeys;
    var orderedFiles = credible
      ? anyDiscPresent
        ? entries.OrderBy(entry => entry.Disc.Value).ThenBy(entry => entry.Track.Value).ThenBy(entry => entry.File.RelativePath, NaturalPaths).Select(entry => entry.File).ToArray()
        : entries.OrderBy(entry => entry.Track.Value).ThenBy(entry => entry.File.RelativePath, NaturalPaths).Select(entry => entry.File).ToArray()
      : naturalFiles;
    return new OrderCandidate(OrderCandidateId.Metadata, orderedFiles.Select(FileId), credible, credible ? OrderConfidence.Medium : OrderConfidence.Low, evidence);
  }

  private static TagNumber ParsePositiveTag(SourceFile file, IEnumerable<string> keys)
  {
    foreach (var key in keys)
    {
      if (!file.ProbeResult.FormatTags.TryGetValue(key, out var value)) continue;
      var numericPart = value.Split('/', 2)[0].Trim();
      return int.TryParse(numericPart, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? new TagNumber(true, true, number) : new TagNumber(true, false, 0);
    }
    return new TagNumber(false, false, 0);
  }

  private static bool SameOrder(OrderCandidate left, OrderCandidate right) => left.FileIds.SequenceEqual(right.FileIds, StringComparer.Ordinal);
  private static string FileId(SourceFile file) => file.RelativePath;
  private static OrderEvidence Evidence(string code, SourceFile file) => new(code, Array.AsReadOnly(new[] { FileId(file) }));
  private sealed record MetadataEntry(SourceFile File, TagNumber Track, TagNumber Disc);
  private sealed record TagNumber(bool IsPresent, bool IsValid, int Value);
}
