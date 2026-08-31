using AudiobookConverter.Core.Analysis;
using AudiobookConverter.Core.Discovery;
using System.Diagnostics;

namespace AudiobookConverter.Core.Tests.Discovery;

public sealed class SourceDiscovererTests : IDisposable
{
  private readonly TemporaryDirectory _fixture = new();

  [Fact]
  public async Task DiscoverAsync_recurses_and_returns_files_in_deterministic_relative_path_order()
  {
    _fixture.CreateFile("CD2/02.m4a");
    _fixture.CreateFile("CD1/01.mp3");
    _fixture.CreateFile("CD2/01.flac");

    var result = await Discover().DiscoverAsync(_fixture.Root, CancellationToken.None);

    Assert.Equal((IEnumerable<string>)["CD1/01.mp3", "CD2/01.flac", "CD2/02.m4a"], result.Files.Select(file => file.RelativePath));
    Assert.All(result.Files, file => Assert.Equal(Path.GetFullPath(file.FullPath), file.FullPath));
  }

  [Fact]
  public async Task DiscoverAsync_accepts_a_single_supported_file()
  {
    var path = _fixture.CreateFile("chapter.opus");

    var result = await Discover().DiscoverAsync(path, CancellationToken.None);

    var file = Assert.Single(result.Files);
    Assert.Equal("chapter.opus", file.RelativePath);
    Assert.Equal(Path.GetFullPath(path), file.FullPath);
  }

  [Fact]
  public async Task DiscoverAsync_accepts_case_insensitive_extensions_and_unicode_paths()
  {
    _fixture.CreateFile("Disco ñ/01.MP3");
    _fixture.CreateFile("Disco ñ/02.WmA");

    var result = await Discover().DiscoverAsync(_fixture.Root, CancellationToken.None);

    Assert.Equal((IEnumerable<string>)["Disco ñ/01.MP3", "Disco ñ/02.WmA"], result.Files.Select(file => file.RelativePath));
  }

  [Fact]
  public async Task DiscoverAsync_ignores_partial_and_unsupported_files()
  {
    _fixture.CreateFile("01.mp3.partial");
    _fixture.CreateFile("02.txt");
    _fixture.CreateFile("03.mp3");

    var result = await Discover().DiscoverAsync(_fixture.Root, CancellationToken.None);

    Assert.Equal((IEnumerable<string>)["03.mp3"], result.Files.Select(file => file.RelativePath));
  }

  [Fact]
  public async Task DiscoverAsync_reports_an_unsupported_single_file_input()
  {
    var path = _fixture.CreateFile("notes.txt");

    var result = await Discover().DiscoverAsync(path, CancellationToken.None);

    var error = Assert.Single(result.Errors);
    Assert.Equal("discovery.input-unsupported", error.Code);
    Assert.Equal(Path.GetFullPath(path), error.FullPath);
  }

  [Fact]
  public async Task DiscoverAsync_does_not_follow_a_reparse_point_directory_input()
  {
    var external = new TemporaryDirectory();
    var link = Path.Combine(_fixture.Root, "external");
    try
    {
      external.CreateFile("outside.mp3");
      await CreateJunctionAsync(link, external.Root);

      var result = await Discover().DiscoverAsync(link, CancellationToken.None);

      Assert.Empty(result.Files);
      Assert.Contains(result.Warnings, warning => warning.Code == "discovery.reparse-point-skipped" && warning.FullPath == Path.GetFullPath(link));
    }
    finally { if (Directory.Exists(link)) Directory.Delete(link); external.Dispose(); }
  }

  [Fact]
  public async Task DiscoverAsync_does_not_probe_a_file_input_reached_through_a_reparse_point()
  {
    var external = new TemporaryDirectory();
    var link = Path.Combine(_fixture.Root, "external");
    try
    {
      var outside = external.CreateFile("outside.mp3");
      await CreateJunctionAsync(link, external.Root);

      var result = await Discover().DiscoverAsync(Path.Combine(link, Path.GetFileName(outside)), CancellationToken.None);

      Assert.Empty(result.Files);
      Assert.Contains(result.Warnings, warning => warning.Code == "discovery.reparse-point-skipped" && warning.FullPath == Path.GetFullPath(link));
    }
    finally { if (Directory.Exists(link)) Directory.Delete(link); external.Dispose(); }
  }

  [Fact]
  public async Task DiscoverAsync_deduplicates_hard_links_and_probes_the_deterministic_path()
  {
    var first = _fixture.CreateFile("01.mp3");
    await CreateHardLinkAsync(Path.Combine(_fixture.Root, "02.mp3"), first);
    var probe = new TestMediaProbe();

    var result = await new SourceDiscoverer(probe, analysisConcurrency: 2).DiscoverAsync(_fixture.Root, CancellationToken.None);

    var file = Assert.Single(result.Files);
    Assert.Equal("01.mp3", file.RelativePath);
    Assert.Equal(Path.GetFullPath(first), file.FullPath);
    Assert.Equal((IEnumerable<string>)[Path.GetFullPath(first)], probe.Inputs);
  }
  [Fact]
  public async Task DiscoverAsync_deduplicates_normalized_file_paths()
  {
    var path = _fixture.CreateFile("chapter.mp3");

    var result = await Discover().DiscoverAsync(Path.Combine(_fixture.Root, ".", Path.GetFileName(path)), CancellationToken.None);

    Assert.Single(result.Files);
  }

  [Fact]
  public async Task DiscoverAsync_reports_unreadable_input_as_a_structured_error()
  {
    var missing = Path.Combine(_fixture.Root, "missing");

    var result = await Discover().DiscoverAsync(missing, CancellationToken.None);

    var error = Assert.Single(result.Errors);
    Assert.Equal("discovery.input-unreadable", error.Code);
    Assert.Equal(Path.GetFullPath(missing), error.FullPath);
  }

  [Fact]
  public async Task DiscoverAsync_recurses_without_following_external_reparse_points()
  {
    _fixture.CreateFile("CD1/01.mp3");
    _fixture.CreateFile("CD2/01.mp3");
    var external = new TemporaryDirectory();
    try
    {
      external.CreateFile("outside.mp3");
      await CreateJunctionAsync(Path.Combine(_fixture.Root, "external"), external.Root);

      var result = await Discover().DiscoverAsync(_fixture.Root, CancellationToken.None);

      Assert.Equal((IEnumerable<string>)["CD1/01.mp3", "CD2/01.mp3"], result.Files.Select(file => file.RelativePath));
      Assert.Contains(result.Warnings, warning => warning.Code == "discovery.reparse-point-skipped");
    }
    finally { if (Directory.Exists(Path.Combine(_fixture.Root, "external"))) Directory.Delete(Path.Combine(_fixture.Root, "external")); external.Dispose(); }
  }

  [Fact]
  public async Task DiscoverAsync_reports_probe_failures_without_stopping_other_files()
  {
    _fixture.CreateFile("01.mp3");
    _fixture.CreateFile("02.mp3");
    var probe = new TestMediaProbe(path => Path.GetFileName(path) == "01.mp3" ? new InvalidOperationException("bad media") : null);

    var result = await new SourceDiscoverer(probe, analysisConcurrency: 2).DiscoverAsync(_fixture.Root, CancellationToken.None);

    Assert.Equal((IEnumerable<string>)["02.mp3"], result.Files.Select(file => file.RelativePath));
    var error = Assert.Single(result.Errors);
    Assert.Equal("discovery.probe-failed", error.Code);
    Assert.Equal("01.mp3", error.RelativePath);
  }

  [Fact]
  public async Task DiscoverAsync_propagates_cancellation()
  {
    _fixture.CreateFile("chapter.mp3");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Discover().DiscoverAsync(_fixture.Root, cancellation.Token));
  }

  [Fact]
  public void Constructor_rejects_nonpositive_analysis_concurrency()
  {
    Assert.Throws<ArgumentOutOfRangeException>(() => new SourceDiscoverer(new TestMediaProbe(), analysisConcurrency: 0));
  }

  private static SourceDiscoverer Discover() => new(new TestMediaProbe(), analysisConcurrency: 2);

  public void Dispose() => _fixture.Dispose();

  private static async Task CreateHardLinkAsync(string linkPath, string targetPath)
  {
    using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{linkPath}\" \"{targetPath}\"") { CreateNoWindow = true, RedirectStandardError = true, UseShellExecute = false })!;
    await process.WaitForExitAsync();
    Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
  }
  private static async Task CreateJunctionAsync(string linkPath, string targetPath)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
    using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"") { CreateNoWindow = true, RedirectStandardError = true, UseShellExecute = false })!;
    await process.WaitForExitAsync();
    Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
  }
  private sealed class TestMediaProbe(Func<string, Exception?>? failure = null) : IMediaProbe
  {
    public List<string> Inputs { get; } = [];

    public Task<MediaProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();
      Inputs.Add(inputPath);
      if (failure?.Invoke(inputPath) is { } exception) throw exception;
      return Task.FromResult(new MediaProbeResult([], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1));
    }
  }

  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory() => Root = Path.Combine(Path.GetTempPath(), $"AudiobookConverter-{Guid.NewGuid():N}");
    public string Root { get; }
    public string CreateFile(string relativePath)
    {
      var path = Path.Combine(Root, relativePath);
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllText(path, "audio");
      return path;
    }
    public void Dispose()
    {
      if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
  }
}
