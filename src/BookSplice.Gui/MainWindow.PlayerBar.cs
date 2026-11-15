using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui;

public partial class MainWindow
{
  private readonly DispatcherTimer _playerTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
  private bool _updatingSlider;
  private bool _draggingSlider;

  internal DispatcherTimer PlayerTimer => _playerTimer;

  private void InitializePlayerBar()
  {
    if (_model is null) return;
    _model.Preview.PropertyChanged += (_, _) => UpdatePlayerBar();
    _playerTimer.Tick += (_, _) => OnPlayerTimerTick();
    UpdatePlayerBar();
  }

  /// <summary>Re-reads the player position and moves the slider. The timer calls this while something plays.</summary>
  internal void OnPlayerTimerTick() => _model?.Preview.Tick();

  // The index in the analysis of the track the Play button would start, or -1. Looks the selected name up, because the list can hold edits that were not applied yet.
  private int PlayTargetIndex()
  {
    if (Queue.SelectedItem is not BookQueueItemViewModel { Analysis: { } analysis }) return -1;
    var name = IncludedList.SelectedItem as string ?? (Queue.SelectedItem as BookQueueItemViewModel)?.IncludedTracks.FirstOrDefault();
    if (name is null) return -1;
    for (var index = 0; index < analysis.OrderedFiles.Count; index++)
      if (string.Equals(analysis.OrderedFiles[index].RelativePath, name, StringComparison.OrdinalIgnoreCase)) return index;
    return -1;
  }

  private void UpdatePlayerBar()
  {
    if (SplitHereButton is null) return;
    var preview = _model?.Preview;
    var canPlay = preview is { CanPlay: true };
    PlayButton.IsEnabled = canPlay && PlayTargetIndex() >= 0;
    PlayBoundaryButton.IsEnabled = canPlay && ChapterGrid.SelectedItem is ChapterRow;
    StopButton.IsEnabled = preview is { CanStop: true };
    SplitHereButton.Visibility = preview is { IsPlaying: true } ? Visibility.Visible : Visibility.Collapsed;
    PlayerSlider.IsEnabled = preview is { IsPlaying: true };
    PlayerPosition.Text = preview?.PositionText ?? "";
    PlayerMessage.Text = preview?.Message ?? "";
    if (preview is { IsPlaying: true }) { if (!_playerTimer.IsEnabled) _playerTimer.Start(); }
    else _playerTimer.Stop();
    if (_draggingSlider) return;
    _updatingSlider = true;
    try
    {
      PlayerSlider.Maximum = Math.Max(1, (preview?.RangeLengthMicroseconds ?? 0) / 1_000_000d);
      PlayerSlider.Value = (preview?.RangePositionMicroseconds ?? 0) / 1_000_000d;
    }
    finally { _updatingSlider = false; }
  }

  private async void Play_Click(object sender, RoutedEventArgs e)
  {
    if (_model is null || PlayTargetIndex() is var index && index < 0) return;
    await _model.Preview.PlayTrackAsync(index);
  }

  private async void PlayBoundary_Click(object sender, RoutedEventArgs e)
  {
    if (_model is null || ChapterGrid.SelectedItem is not ChapterRow row) return;
    await _model.Preview.PlayBoundaryAsync(row.StartMicroseconds);
  }

  private void Stop_Click(object sender, RoutedEventArgs e) => _model?.Preview.Stop();

  private void PlayerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
  {
    if (_updatingSlider || _draggingSlider) return;
    SeekToSlider();
  }

  private void PlayerSlider_DragStarted(object sender, DragStartedEventArgs e) => _draggingSlider = true;

  private void PlayerSlider_DragCompleted(object sender, DragCompletedEventArgs e)
  {
    _draggingSlider = false;
    SeekToSlider();
  }

  // The last microsecond is the most a seek can ask for: the end of a window is outside it and would restart a whole track window.
  private async void SeekToSlider()
  {
    if (_model?.Preview is not { IsPlaying: true } preview) return;
    var offset = Math.Min((long)Math.Round(PlayerSlider.Value * 1_000_000d), Math.Max(0, preview.RangeLengthMicroseconds - 1));
    await preview.SeekAsync(preview.RangeStartMicroseconds + offset);
  }

  private void SplitHere_Click(object sender, RoutedEventArgs e)
  {
    if (_model?.Preview is not { IsPlaying: true } preview || ChapterBook is not { } book) return;
    var at = preview.CurrentBookMicroseconds;
    var index = -1;
    foreach (var row in book.ChapterRows)
      if (row.StartMicroseconds <= at) index = row.Index;
    if (index < 0) return;
    if (ApplyChapterEdit(index, item => item.Split(index, at))) SelectChapter(index + 1);
  }
}
