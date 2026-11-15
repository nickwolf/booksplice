using System.IO;
using BookSplice.Core.Analysis;
using BookSplice.Core.Preview;

namespace BookSplice.Gui.ViewModels;

/// <summary>
/// Plays bounded windows of the selected book. Every public member runs on the UI thread and awaits resume there.
/// Each start, seek and stop bumps a generation and cancels the previous token, so a late decode is discarded.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Release disposes the token source; the owning view model calls it on close.")]
public sealed class PreviewViewModel(IPreviewDecoder decoder, Func<IAudioPlayer> playerFactory, string previewRoot) : ObservableModel
{
  public const string UnavailableMessage = PreviewUnavailableException.DefaultMessage;

  private sealed record Window(PreviewRegion Region, string Path, int TrackIndex);

  private readonly string _sessionDirectory = Path.Combine(previewRoot, Guid.NewGuid().ToString("N"));
  private List<PreviewTrack> _tracks = [];
  private IAudioPlayer? _player;
  private CancellationTokenSource? _cts;
  private int _generation;
  private int _fileCounter;
  private Window? _current;
  private Window? _next;
  private bool _aheadPlanned;
  private bool _aheadFailed;
  private bool _waiting;
  private bool _canPlay;
  private bool _isPlaying;
  private bool _isDecoding;
  private bool _isBoundary;
  private long _positionMicroseconds;
  private string _message = "";
  private Task _ahead = Task.CompletedTask;

  public bool CanPlay { get => _canPlay; private set { if (_canPlay == value) return; _canPlay = value; Changed(); } }
  public bool IsPlaying { get => _isPlaying; private set { if (_isPlaying == value) return; _isPlaying = value; Changed(); } }
  public bool IsDecoding { get => _isDecoding; private set { if (_isDecoding == value) return; _isDecoding = value; Changed(); } }
  public bool IsBoundary { get => _isBoundary; private set { if (_isBoundary == value) return; _isBoundary = value; Changed(); } }
  public string Message { get => _message; private set { if (_message == value) return; _message = value; Changed(); } }
  public long CurrentBookMicroseconds => _current is null ? 0 : _current.Region.BookStartMicroseconds + _positionMicroseconds;
  public string PositionText => _current is null ? "" : $"{Format(TrackOffset + _positionMicroseconds)} / {Format(IsBoundary ? _current.Region.LengthMicroseconds : _tracks[_current.TrackIndex].DurationMicroseconds)}";

  /// <summary>The decode-ahead in flight, for tests.</summary>
  internal Task AheadTask => _ahead;

  private long TrackOffset => _current is null || IsBoundary ? 0 : _current.Region.BookStartMicroseconds - PreviewPlanner.TrackStart(_tracks, _current.TrackIndex);

  /// <summary>Deletes what an earlier run left in the preview folder. Files that cannot be deleted are skipped.</summary>
  public static void Sweep(string previewRoot)
  {
    try
    {
      if (!Directory.Exists(previewRoot)) return;
      foreach (var entry in new DirectoryInfo(previewRoot).EnumerateFileSystemInfos())
      {
        try
        {
          if (entry is DirectoryInfo directory) directory.Delete(true);
          else entry.Delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
      }
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
  }

  /// <summary>A preview that never plays, for a window without media tools.</summary>
  public static PreviewViewModel CreateUnavailable() => new(new UnavailableDecoder(), () => throw new NotSupportedException("Preview is not available."), Path.Combine(Path.GetTempPath(), "BookSplice-preview-unavailable"));

  private sealed class UnavailableDecoder : IPreviewDecoder
  {
    public Task DecodeAsync(PreviewRegion region, string outputWavPath, CancellationToken cancellationToken) => throw new PreviewUnavailableException();
  }

  public void SetBook(BookQueueItemViewModel? book)
  {
    Teardown();
    Message = "";
    _tracks = book?.Analysis is { Status: BookAnalysisStatus.Ready } analysis ? TracksOf(analysis) : [];
    CanPlay = _tracks.Count > 0 && _tracks.Sum(track => track.DurationMicroseconds) > 0;
  }

  public void Stop() => Teardown();

  public void Release()
  {
    Teardown();
    _tracks = [];
    CanPlay = false;
  }

  public async Task PlayTrackAsync(int trackIndex, long offsetMicroseconds = 0)
  {
    if (!CanPlay || trackIndex < 0 || trackIndex >= _tracks.Count) return;
    var duration = _tracks[trackIndex].DurationMicroseconds;
    if (duration <= 0) { Teardown(); Message = UnavailableMessage; return; }
    var offset = Math.Clamp(offsetMicroseconds, 0, duration - 1);
    await StartAsync(PreviewPlanner.TrackWindow(_tracks, trackIndex, offset), trackIndex, boundary: false);
  }

  public async Task PlayBoundaryAsync(long bookMicroseconds)
  {
    if (!CanPlay) return;
    var region = PreviewPlanner.AroundBoundary(_tracks, bookMicroseconds);
    if (region.Pieces.Count == 0) return;
    await StartAsync(region, region.Pieces[0].TrackIndex, boundary: true);
  }

  public async Task SeekAsync(long bookMicroseconds)
  {
    if (!CanPlay) return;
    if (_current is { } window && bookMicroseconds >= window.Region.BookStartMicroseconds && bookMicroseconds < window.Region.BookStartMicroseconds + window.Region.LengthMicroseconds && _player is { } player)
    {
      _positionMicroseconds = bookMicroseconds - window.Region.BookStartMicroseconds;
      player.Position = TimeSpan.FromTicks(_positionMicroseconds * 10);
      if (_waiting)
      {
        _waiting = false;
        IsDecoding = false;
        player.Play();
      }
      RaisePosition();
      return;
    }
    var total = _tracks.Sum(track => track.DurationMicroseconds);
    var book = Math.Clamp(bookMicroseconds, 0, total - 1);
    long start = 0;
    for (var index = 0; index < _tracks.Count; index++)
    {
      var end = start + _tracks[index].DurationMicroseconds;
      if (book < end) { await PlayTrackAsync(index, book - start); return; }
      start = end;
    }
  }

  /// <summary>Re-reads the player position. The view's timer calls this.</summary>
  public void Tick()
  {
    if (_player is null || _current is null) return;
    _positionMicroseconds = _player.Position.Ticks / 10;
    RaisePosition();
  }

  private async Task StartAsync(PreviewRegion region, int trackIndex, bool boundary)
  {
    Teardown();
    Message = "";
    var generation = _generation;
    var cts = _cts = new CancellationTokenSource();
    IsBoundary = boundary;
    IsDecoding = true;
    var window = await DecodeAsync(region, trackIndex, generation, cts.Token);
    if (window is null) return;
    var player = _player = playerFactory();
    player.Ended += OnEnded;
    player.Failed += OnFailed;
    _current = window;
    IsDecoding = false;
    try
    {
      player.Open(window.Path);
      player.Play();
    }
    catch (Exception exception) when (exception is InvalidOperationException or IOException or UriFormatException)
    {
      Teardown();
      Message = UnavailableMessage;
      return;
    }
    IsPlaying = true;
    _positionMicroseconds = 0;
    RaisePosition();
    if (!boundary) StartAhead(generation, cts.Token);
  }

  // Returns the decoded window, or null when the result is stale or failed (already cleaned up and reported).
  private async Task<Window?> DecodeAsync(PreviewRegion region, int trackIndex, int generation, CancellationToken token)
  {
    var path = Path.Combine(_sessionDirectory, $"{++_fileCounter}.wav");
    try
    {
      Directory.CreateDirectory(_sessionDirectory);
      await decoder.DecodeAsync(region, path, token);
    }
    catch (OperationCanceledException)
    {
      DeleteQuietly(path);
      return null;
    }
    catch (Exception exception) when (exception is PreviewUnavailableException or IOException or UnauthorizedAccessException)
    {
      DeleteQuietly(path);
      if (generation == _generation) Fail(generation);
      return null;
    }
    if (generation != _generation)
    {
      DeleteQuietly(path);
      return null;
    }
    return new Window(region, path, trackIndex);
  }

  private void Fail(int generation)
  {
    if (generation != _generation) return;
    Teardown();
    Message = UnavailableMessage;
  }

  // Plans the window after the current one. Does nothing at the end of the track.
  private void StartAhead(int generation, CancellationToken token)
  {
    _aheadPlanned = false;
    _aheadFailed = false;
    if (_current is not { } current) return;
    var offsetEnd = current.Region.BookStartMicroseconds - PreviewPlanner.TrackStart(_tracks, current.TrackIndex) + current.Region.LengthMicroseconds;
    if (offsetEnd >= _tracks[current.TrackIndex].DurationMicroseconds) return;
    _aheadPlanned = true;
    _ahead = AheadAsync(PreviewPlanner.TrackWindow(_tracks, current.TrackIndex, offsetEnd), current.TrackIndex, generation, token);
  }

  private async Task AheadAsync(PreviewRegion region, int trackIndex, int generation, CancellationToken token)
  {
    var path = Path.Combine(_sessionDirectory, $"{++_fileCounter}.wav");
    try
    {
      await decoder.DecodeAsync(region, path, token);
    }
    catch (OperationCanceledException)
    {
      DeleteQuietly(path);
      return;
    }
    catch (Exception exception) when (exception is PreviewUnavailableException or IOException or UnauthorizedAccessException)
    {
      DeleteQuietly(path);
      if (generation != _generation) return;
      _aheadFailed = true;
      Message = UnavailableMessage;
      if (_waiting) Teardown();
      return;
    }
    if (generation != _generation)
    {
      DeleteQuietly(path);
      return;
    }
    _next = new Window(region, path, trackIndex);
    if (_waiting) SwitchToNext();
  }

  private void OnEnded(object? sender, EventArgs e)
  {
    if (!ReferenceEquals(sender, _player)) return;
    if (IsBoundary || !_aheadPlanned || _aheadFailed) { Teardown(); return; }
    if (_next is not null) { SwitchToNext(); return; }
    _waiting = true;
    IsDecoding = true;
  }

  private void OnFailed(object? sender, string message)
  {
    if (!ReferenceEquals(sender, _player)) return;
    Teardown();
    Message = message;
  }

  private void SwitchToNext()
  {
    if (_player is not { } player || _current is not { } old || _next is not { } next || _cts is not { } cts) return;
    _waiting = false;
    IsDecoding = false;
    _next = null;
    _current = next;
    player.Close();
    DeleteQuietly(old.Path);
    try
    {
      player.Open(next.Path);
      player.Play();
    }
    catch (Exception exception) when (exception is InvalidOperationException or IOException or UriFormatException)
    {
      Teardown();
      Message = UnavailableMessage;
      return;
    }
    _positionMicroseconds = 0;
    RaisePosition();
    StartAhead(_generation, cts.Token);
  }

  // Stops playback, cancels decoding, closes the player and deletes the preview files. Keeps the book and leaves Message to the caller.
  private void Teardown()
  {
    _generation++;
    var cts = _cts;
    _cts = null;
    if (cts is not null)
    {
      try { cts.Cancel(); }
      finally { cts.Dispose(); }
    }
    var player = _player;
    _player = null;
    if (player is not null)
    {
      player.Ended -= OnEnded;
      player.Failed -= OnFailed;
      try { player.Stop(); player.Close(); }
      finally { player.Dispose(); }
    }
    if (_current is not null) DeleteQuietly(_current.Path);
    if (_next is not null) DeleteQuietly(_next.Path);
    _current = null;
    _next = null;
    _aheadPlanned = false;
    _aheadFailed = false;
    _waiting = false;
    _positionMicroseconds = 0;
    IsPlaying = false;
    IsDecoding = false;
    IsBoundary = false;
    RaisePosition();
    TryRemoveSessionDirectory();
  }

  private void RaisePosition()
  {
    Changed(nameof(CurrentBookMicroseconds));
    Changed(nameof(PositionText));
  }

  private void TryRemoveSessionDirectory()
  {
    try { if (Directory.Exists(_sessionDirectory)) Directory.Delete(_sessionDirectory, false); }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
  }

  private static void DeleteQuietly(string path)
  {
    try { File.Delete(path); }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
  }

  private static string Format(long microseconds)
  {
    var time = TimeSpan.FromTicks(Math.Max(0, microseconds) * 10);
    return $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
  }

  private static List<PreviewTrack> TracksOf(BookAnalysis analysis)
  {
    var chapters = analysis.Chapters;
    var byChapters = chapters.IsValid && chapters.Entries.Count == analysis.OrderedFiles.Count;
    var tracks = new List<PreviewTrack>(analysis.OrderedFiles.Count);
    foreach (var file in analysis.OrderedFiles)
    {
      long duration;
      var entry = byChapters ? chapters.Entries.FirstOrDefault(candidate => string.Equals(candidate.SourceRelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase)) : null;
      if (entry is not null) duration = entry.EndMicroseconds - entry.StartMicroseconds;
      else duration = file.ProbeResult.Duration is { } seconds ? (long)(seconds * 1_000_000m) : 0;
      tracks.Add(new PreviewTrack(file.FullPath, Math.Max(0, duration)));
    }
    return tracks;
  }
}
