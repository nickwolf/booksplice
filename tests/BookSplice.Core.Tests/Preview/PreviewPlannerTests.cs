using BookSplice.Core.Preview;

namespace BookSplice.Core.Tests.Preview;

public sealed class PreviewPlannerTests
{
  private const long Second = 1_000_000;

  private static PreviewTrack[] Tracks(params long[] seconds) => seconds.Select((value, index) => new PreviewTrack($"t{index}.mp3", value * Second)).ToArray();

  [Fact]
  public void SingleTrackRangeIsOnePiece()
  {
    var region = PreviewPlanner.ForBookRange(Tracks(60), 10 * Second, 20 * Second);
    Assert.Equal(10 * Second, region.BookStartMicroseconds);
    Assert.Equal(20 * Second, region.LengthMicroseconds);
    Assert.Equal([new PreviewPiece(0, "t0.mp3", 10 * Second, 20 * Second)], region.Pieces);
  }

  [Fact]
  public void WindowIsTwoMinutesAndClampedAtTrackEnd()
  {
    var full = PreviewPlanner.TrackWindow(Tracks(600, 600), 0, 30 * Second);
    Assert.Equal(PreviewPlanner.WindowMicroseconds, full.LengthMicroseconds);
    Assert.Single(full.Pieces);
    var tail = PreviewPlanner.TrackWindow(Tracks(100, 600), 0, 70 * Second);
    Assert.Equal(30 * Second, tail.LengthMicroseconds);
    Assert.Equal([new PreviewPiece(0, "t0.mp3", 70 * Second, 30 * Second)], tail.Pieces);
  }

  [Fact]
  public void WindowOnLaterTrackStartsAtItsBookTime()
  {
    var region = PreviewPlanner.TrackWindow(Tracks(100, 50), 1, 10 * Second);
    Assert.Equal(110 * Second, region.BookStartMicroseconds);
    Assert.Equal(40 * Second, region.LengthMicroseconds);
    Assert.Equal(1, region.Pieces[0].TrackIndex);
  }

  [Theory]
  [InlineData(100, 100)]
  [InlineData(100, 500)]
  [InlineData(100, -1)]
  public void WindowOffsetOutsideTrackThrows(long duration, long offset)
  {
    Assert.Throws<ArgumentOutOfRangeException>(() => PreviewPlanner.TrackWindow(Tracks(duration), 0, offset * Second));
  }

  [Fact]
  public void RangeSpansTwoTracks()
  {
    var region = PreviewPlanner.ForBookRange(Tracks(12, 12), 7 * Second, 10 * Second);
    Assert.Equal([new PreviewPiece(0, "t0.mp3", 7 * Second, 5 * Second), new PreviewPiece(1, "t1.mp3", 0, 5 * Second)], region.Pieces);
  }

  [Fact]
  public void RangeSpansThreeTracks()
  {
    var region = PreviewPlanner.ForBookRange(Tracks(10, 5, 10), 8 * Second, 10 * Second);
    Assert.Equal([
      new PreviewPiece(0, "t0.mp3", 8 * Second, 2 * Second),
      new PreviewPiece(1, "t1.mp3", 0, 5 * Second),
      new PreviewPiece(2, "t2.mp3", 0, 3 * Second)], region.Pieces);
  }

  [Fact]
  public void RangeIsClampedAtZeroAndAtTotal()
  {
    var low = PreviewPlanner.ForBookRange(Tracks(10, 10), -5 * Second, 8 * Second);
    Assert.Equal(0, low.BookStartMicroseconds);
    Assert.Equal(8 * Second, low.LengthMicroseconds);
    var high = PreviewPlanner.ForBookRange(Tracks(10, 10), 15 * Second, 100 * Second);
    Assert.Equal(5 * Second, high.LengthMicroseconds);
    Assert.Equal([new PreviewPiece(1, "t1.mp3", 5 * Second, 5 * Second)], high.Pieces);
    Assert.Empty(PreviewPlanner.ForBookRange(Tracks(10), 10 * Second, Second).Pieces);
  }

  [Fact]
  public void ZeroLengthTracksAreSkipped()
  {
    var region = PreviewPlanner.ForBookRange(Tracks(5, 0, 5), 3 * Second, 4 * Second);
    Assert.Equal([new PreviewPiece(0, "t0.mp3", 3 * Second, 2 * Second), new PreviewPiece(2, "t2.mp3", 0, 2 * Second)], region.Pieces);
  }

  [Fact]
  public void AroundBoundaryUsesFiveSecondsEachSideAndSpansTracks()
  {
    var region = PreviewPlanner.AroundBoundary(Tracks(12, 12), 12 * Second);
    Assert.Equal(7 * Second, region.BookStartMicroseconds);
    Assert.Equal(10 * Second, region.LengthMicroseconds);
    Assert.Equal([new PreviewPiece(0, "t0.mp3", 7 * Second, 5 * Second), new PreviewPiece(1, "t1.mp3", 0, 5 * Second)], region.Pieces);
  }

  [Fact]
  public void AroundBoundaryClampsToTheBook()
  {
    var start = PreviewPlanner.AroundBoundary(Tracks(12, 12), 2 * Second);
    Assert.Equal(0, start.BookStartMicroseconds);
    Assert.Equal(7 * Second, start.LengthMicroseconds);
    var end = PreviewPlanner.AroundBoundary(Tracks(12, 12), 22 * Second);
    Assert.Equal(17 * Second, end.BookStartMicroseconds);
    Assert.Equal(7 * Second, end.LengthMicroseconds);
  }

  [Fact]
  public void HundredHourTimelineIsExact()
  {
    var hour = 3600 * Second;
    var tracks = Enumerable.Range(0, 100).Select(index => new PreviewTrack($"h{index}", hour)).ToArray();
    Assert.Equal(99 * hour, PreviewPlanner.TrackStart(tracks, 99));
    var region = PreviewPlanner.AroundBoundary(tracks, 50 * hour);
    Assert.Equal(50 * hour - 5 * Second, region.BookStartMicroseconds);
    Assert.Equal(10 * Second, region.LengthMicroseconds);
    Assert.Equal([49, 50], region.Pieces.Select(piece => piece.TrackIndex));
    Assert.Equal(hour - 5 * Second, region.Pieces[0].OffsetMicroseconds);
    Assert.Equal(5 * Second, region.Pieces[1].LengthMicroseconds);
  }

  [Fact]
  public void OverflowThrowsInsteadOfWrapping()
  {
    var big = new[] { new PreviewTrack("a", long.MaxValue), new PreviewTrack("b", 1) };
    Assert.Throws<OverflowException>(() => PreviewPlanner.ForBookRange(big, 0, 1));
    Assert.Throws<OverflowException>(() => PreviewPlanner.AroundBoundary(Tracks(10), long.MaxValue));
    Assert.Throws<OverflowException>(() => PreviewPlanner.ForBookRange([new PreviewTrack("a", long.MaxValue)], long.MaxValue - 1, long.MaxValue));
  }

  [Fact]
  public void EmptyOrSilentBookThrows()
  {
    Assert.Throws<ArgumentException>(() => PreviewPlanner.ForBookRange([], 0, 1));
    Assert.Throws<ArgumentException>(() => PreviewPlanner.AroundBoundary(Tracks(0, 0), 0));
    Assert.Throws<ArgumentException>(() => PreviewPlanner.TrackWindow([], 0, 0));
  }
}
