using BookSplice.Core.Preview;

namespace BookSplice.Gui.Tests;

internal sealed class FakeDecoder : IPreviewDecoder
{
  private readonly Queue<FakeDecodeCall> _expected = new();
  public List<FakeDecodeCall> Calls { get; } = [];
  public bool AutoComplete { get; set; }
  /// <summary>When false the decode finishes even if cancelled, like a decoder that was already done.</summary>
  public bool HonorCancel { get; set; } = true;
  public Exception? Failure { get; set; }

  public FakeDecodeCall Expect()
  {
    var call = new FakeDecodeCall();
    _expected.Enqueue(call);
    return call;
  }

  public Task DecodeAsync(PreviewRegion region, string outputWavPath, CancellationToken ct)
  {
    if (Failure is { } failure) return Task.FromException(failure);
    var call = _expected.Count > 0 ? _expected.Dequeue() : new FakeDecodeCall();
    call.Region = region;
    call.Path = outputWavPath;
    call.Token = ct;
    call.HonorCancel = HonorCancel;
    Calls.Add(call);
    if (AutoComplete && !call.IsCompleted) call.Complete();
    return call.Task(ct);
  }
}

internal sealed class FakeDecodeCall
{
  private readonly TaskCompletionSource _source = new();
  public PreviewRegion Region { get; set; } = null!;
  public string Path { get; set; } = "";
  public CancellationToken Token { get; set; }
  public bool HonorCancel { get; set; } = true;
  public bool IsCompleted => _source.Task.IsCompleted;

  public void Complete()
  {
    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
    File.WriteAllBytes(Path, [1, 2, 3]);
    _source.TrySetResult();
  }

  public Task Task(CancellationToken ct)
  {
    if (HonorCancel) ct.Register(() => _source.TrySetCanceled(ct));
    return _source.Task;
  }
}

// Holds the opened file like MediaPlayer does, so deleting it before Close fails.
internal sealed class FakePlayer : IAudioPlayer
{
  private FileStream? _held;
  public List<string> Calls { get; } = [];
  public string? OpenedPath { get; private set; }
  public bool IsDisposed { get; private set; }
  private TimeSpan _position;
  public List<TimeSpan> PositionSets { get; } = [];
  public TimeSpan Position { get => _position; set { _position = value; PositionSets.Add(value); } }
  public event EventHandler? Opened;
  public event EventHandler? Ended;
  public event EventHandler<string>? Failed;

  public void Open(string path)
  {
    Calls.Add("Open");
    OpenedPath = path;
    _held?.Dispose();
    _held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    _position = TimeSpan.Zero;
  }

  public void Play() => Calls.Add("Play");
  public void Stop() => Calls.Add("Stop");

  public void Close()
  {
    Calls.Add("Close");
    _held?.Dispose();
    _held = null;
  }

  public void Dispose()
  {
    IsDisposed = true;
    _held?.Dispose();
    _held = null;
  }

  public void RaiseOpened() => Opened?.Invoke(this, EventArgs.Empty);
  public void RaiseEnded() => Ended?.Invoke(this, EventArgs.Empty);
  public void RaiseFailed(string message) => Failed?.Invoke(this, message);
}
