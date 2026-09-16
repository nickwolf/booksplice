using AudiobookConverter.FFmpeg.Execution;

namespace AudiobookConverter.FFmpeg.Tests.Execution;

public sealed class TemporaryArtifactCleanerTests
{
  [Fact]
  public async Task CleanupRemovesTheOwnedJobDirectory()
  {
    using var root = new TemporaryDirectory();
    var job = Directory.CreateDirectory(Path.Combine(root.Path, "job")).FullName;
    var output = Path.Combine(job, "output.m4b");
    File.WriteAllText(output, "output");
    File.WriteAllText(Path.Combine(job, "metadata.ffmeta"), "metadata");

    await new TemporaryArtifactCleaner(root.Path).CleanupAsync(output, CancellationToken.None);

    Assert.False(Directory.Exists(job));
    Assert.True(Directory.Exists(root.Path));
  }

  [Fact]
  public async Task CleanupIgnoresAPathOutsideTheConfiguredTemporaryRoot()
  {
    using var root = new TemporaryDirectory();
    using var outside = new TemporaryDirectory();
    var output = Path.Combine(outside.Path, "output.m4b");
    File.WriteAllText(output, "published");

    await new TemporaryArtifactCleaner(root.Path).CleanupAsync(output, CancellationToken.None);

    Assert.True(File.Exists(output));
  }

  [Fact]
  public async Task CleanupRefusesAWorkspaceContainingAnUnexpectedDirectory()
  {
    using var root = new TemporaryDirectory();
    var job = Directory.CreateDirectory(Path.Combine(root.Path, "job")).FullName;
    var output = Path.Combine(job, "output.m4b");
    File.WriteAllText(output, "output");
    var unexpected = Directory.CreateDirectory(Path.Combine(job, "unexpected")).FullName;
    var sentinel = Path.Combine(unexpected, "keep.txt");
    File.WriteAllText(sentinel, "keep");

    await new TemporaryArtifactCleaner(root.Path).CleanupAsync(output, CancellationToken.None);

    Assert.True(File.Exists(sentinel));
    Assert.True(File.Exists(output));
  }

  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"abc-cleaner-{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
  }
}
