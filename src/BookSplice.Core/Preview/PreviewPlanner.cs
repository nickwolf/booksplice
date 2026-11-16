namespace BookSplice.Core.Preview;

public sealed record PreviewTrack(string Path, long DurationMicroseconds);

public sealed record PreviewPiece(int TrackIndex, string Path, long OffsetMicroseconds, long LengthMicroseconds);

public sealed record PreviewRegion(long BookStartMicroseconds, long LengthMicroseconds, IReadOnlyList<PreviewPiece> Pieces);

public sealed class PreviewUnavailableException : Exception
{
  public const string DefaultMessage = "This track can't be previewed.";

  public PreviewUnavailableException(string toolOutput = "", Exception? innerException = null) : base(DefaultMessage, innerException)
  {
    ToolOutput = toolOutput;
  }

  /// <summary>Raw tool diagnostics. Deliberately kept out of <see cref="Exception.Message"/>.</summary>
  public string ToolOutput { get; }
}

/// <summary>Plans bounded preview regions over tracks laid end to end. Pure arithmetic, no IO.</summary>
public static class PreviewPlanner
{
  public const long WindowMicroseconds = 120_000_000;
  public const long BoundarySideMicroseconds = 5_000_000;

  /// <summary>Book time where the track at <paramref name="index"/> starts.</summary>
  public static long TrackStart(IReadOnlyList<PreviewTrack> tracks, int index)
  {
    ArgumentNullException.ThrowIfNull(tracks);
    ArgumentOutOfRangeException.ThrowIfNegative(index);
    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, tracks.Count);
    long start = 0;
    for (var i = 0; i < index; i++) start = checked(start + Duration(tracks[i]));
    return start;
  }

  /// <summary>The part of the book in [start, start + length), clamped to the book. A start at or past the end yields no pieces.</summary>
  public static PreviewRegion ForBookRange(IReadOnlyList<PreviewTrack> tracks, long startMicroseconds, long lengthMicroseconds)
  {
    var total = Total(tracks);
    ArgumentOutOfRangeException.ThrowIfNegative(lengthMicroseconds);
    var start = Math.Clamp(startMicroseconds, 0, total);
    var end = Math.Min(checked(start + lengthMicroseconds), total);
    return Build(tracks, start, end);
  }

  /// <summary>Up to <see cref="WindowMicroseconds"/> of one track from the offset, never crossing into the next track.</summary>
  public static PreviewRegion TrackWindow(IReadOnlyList<PreviewTrack> tracks, int trackIndex, long offsetInTrackMicroseconds)
  {
    Total(tracks);
    if (trackIndex < 0 || trackIndex >= tracks.Count) throw new ArgumentOutOfRangeException(nameof(trackIndex));
    var duration = Duration(tracks[trackIndex]);
    if (offsetInTrackMicroseconds < 0 || offsetInTrackMicroseconds >= duration) throw new ArgumentOutOfRangeException(nameof(offsetInTrackMicroseconds));
    var length = Math.Min(WindowMicroseconds, duration - offsetInTrackMicroseconds);
    var start = checked(TrackStart(tracks, trackIndex) + offsetInTrackMicroseconds);
    return Build(tracks, start, checked(start + length));
  }

  /// <summary>[t - 5 s, t + 5 s) clamped to the book. May span tracks.</summary>
  public static PreviewRegion AroundBoundary(IReadOnlyList<PreviewTrack> tracks, long bookMicroseconds)
  {
    var total = Total(tracks);
    var start = Math.Clamp(checked(bookMicroseconds - BoundarySideMicroseconds), 0, total);
    var end = Math.Clamp(checked(bookMicroseconds + BoundarySideMicroseconds), 0, total);
    return Build(tracks, start, Math.Max(start, end));
  }

  private static long Duration(PreviewTrack track)
  {
    if (track.DurationMicroseconds < 0) throw new ArgumentException("A track duration cannot be negative.", nameof(track));
    return track.DurationMicroseconds;
  }

  private static long Total(IReadOnlyList<PreviewTrack> tracks)
  {
    ArgumentNullException.ThrowIfNull(tracks);
    long total = 0;
    foreach (var track in tracks) total = checked(total + Duration(track));
    if (tracks.Count == 0 || total == 0) throw new ArgumentException("There is no audio to preview.", nameof(tracks));
    return total;
  }

  private static PreviewRegion Build(IReadOnlyList<PreviewTrack> tracks, long start, long end)
  {
    var pieces = new List<PreviewPiece>();
    long trackStart = 0;
    for (var i = 0; i < tracks.Count && trackStart < end; i++)
    {
      var trackEnd = checked(trackStart + tracks[i].DurationMicroseconds);
      var from = Math.Max(start, trackStart);
      var to = Math.Min(end, trackEnd);
      if (to > from) pieces.Add(new PreviewPiece(i, tracks[i].Path, from - trackStart, to - from));
      trackStart = trackEnd;
    }
    return new PreviewRegion(start, end - start, pieces);
  }
}
