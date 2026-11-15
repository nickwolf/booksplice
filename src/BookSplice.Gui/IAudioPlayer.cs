namespace BookSplice.Gui;

/// <summary>Plays one audio file at a time. Used only on the UI thread.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716", Justification = "Stop mirrors MediaPlayer.Stop; the interface is only implemented in C#.")]
public interface IAudioPlayer : IDisposable
{
  void Open(string path);
  void Play();
  void Stop();
  /// <summary>Releases the open file so it can be deleted.</summary>
  void Close();
  TimeSpan Position { get; set; }
  event EventHandler? Ended;
  event EventHandler<string>? Failed;
}
