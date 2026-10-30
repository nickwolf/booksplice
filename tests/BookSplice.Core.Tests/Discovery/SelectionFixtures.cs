using System.Security.Cryptography;
using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;

namespace BookSplice.Core.Tests.Discovery;

internal sealed class SelectionTree : IDisposable
{
  public string Root { get; } = Path.Combine(Path.GetTempPath(), $"BookSplice-{Guid.NewGuid():N}");

  public string Write(string relativePath, byte[]? content = null)
  {
    var path = Path.GetFullPath(Path.Combine(Root, relativePath));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, content ?? System.Text.Encoding.UTF8.GetBytes("audio:" + relativePath));
    return path;
  }

  public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

  public void Dispose()
  {
    if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
  }

  public static BookAnalyzer Analyzer(RecordingProbe? probe = null)
    => new(new SourceDiscoverer(probe ?? new RecordingProbe(), analysisConcurrency: 2), new OrderResolver(), new MetadataAggregator(), new CoverDiscoverer(), new ChapterPlanner());

  public static byte[] TinyJpeg(int width, int height) => [255, 216, 255, 192, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 17, 0, 2, 17, 0, 3, 17, 0, 255, 217];
}

internal sealed class RecordingProbe : IMediaProbe
{
  private readonly List<string> _inputs = [];
  public IReadOnlyList<string> Inputs { get { lock (_inputs) return _inputs.ToArray(); } }

  public Task<MediaProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken = default)
  {
    lock (_inputs) _inputs.Add(inputPath);
    return Task.FromResult(new MediaProbeResult([new AudioTrack(0, "mp3", "audio", 2, 44100, 1m, new Rational(1, 44100), new Dictionary<string, string>())], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 60m));
  }
}
