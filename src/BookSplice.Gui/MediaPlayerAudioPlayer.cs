using System.Windows.Media;

namespace BookSplice.Gui;

public sealed class MediaPlayerAudioPlayer : IAudioPlayer
{
  private readonly MediaPlayer _player = new();

  public MediaPlayerAudioPlayer()
  {
    _player.MediaOpened += OnOpened;
    _player.MediaEnded += OnEnded;
    _player.MediaFailed += OnFailed;
  }

  public event EventHandler? Opened;
  public event EventHandler? Ended;
  public event EventHandler<string>? Failed;

  public TimeSpan Position { get => _player.Position; set => _player.Position = value; }

  public void Open(string path) => _player.Open(new Uri(path, UriKind.Absolute));
  public void Play() => _player.Play();
  public void Stop() => _player.Stop();
  public void Close() => _player.Close();

  public void Dispose()
  {
    _player.MediaOpened -= OnOpened;
    _player.MediaEnded -= OnEnded;
    _player.MediaFailed -= OnFailed;
    _player.Close();
  }

  private void OnOpened(object? sender, EventArgs e) => Opened?.Invoke(this, EventArgs.Empty);
  private void OnEnded(object? sender, EventArgs e) => Ended?.Invoke(this, EventArgs.Empty);
  private void OnFailed(object? sender, ExceptionEventArgs e) => Failed?.Invoke(this, e.ErrorException?.Message ?? "The preview could not be played.");
}
