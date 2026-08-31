using System.Security.Cryptography;
using AudiobookConverter.Core.Analysis;
using AudiobookConverter.Core.Covers;
using AudiobookConverter.Core.Discovery;

namespace AudiobookConverter.Core.Tests.Covers;

public sealed class CoverDiscovererTests : IDisposable
{
  private readonly TemporaryDirectory _fixture = new();

  [Fact]
  public async Task DiscoverAsync_finds_a_mixed_case_canonical_external_name()
  {
    _fixture.CreateFile("CoVeR.JpG", TinyJpeg(200, 300));

    var result = await Discover().DiscoverAsync(_fixture.Root, [], CancellationToken.None);

    var candidate = Assert.Single(result.Candidates);
    Assert.Equal(CoverOrigin.ExternalFile, candidate.Origin);
    Assert.Equal("CoVeR.JpG", Path.GetFileName(candidate.SourcePath));
  }

  [Fact]
  public async Task DiscoverAsync_prefers_a_canonical_root_external_cover()
  {
    _fixture.CreateFile("cover.jpg", TinyJpeg(100, 100));
    _fixture.CreateFile("other.png", TinyPng(1000, 1000));
    IReadOnlyList<SourceFile> files = [Source("01.mp3", Picture(0, "Cover"))];

    var result = await Discover(TinyPng(500, 500)).DiscoverAsync(_fixture.Root, files, CancellationToken.None);

    Assert.Equal("cover.jpg", Path.GetFileName(result.Selected!.SourcePath));
  }

  [Fact]
  public async Task DiscoverAsync_collapses_repeated_embedded_art_by_payload_hash()
  {
    var bytes = TinyPng(64, 64);
    var result = await Discover(bytes).DiscoverAsync(_fixture.Root, [Source("01.mp3", Picture(0, "Cover")), Source("02.mp3", Picture(0, "Cover"))], CancellationToken.None);

    var candidate = Assert.Single(result.Candidates);
    Assert.Equal(CoverOrigin.EmbeddedPicture, candidate.Origin);
  }

  [Fact]
  public async Task DiscoverAsync_collapses_byte_identical_external_aliases()
  {
    var bytes = TinyPng(64, 64);
    _fixture.CreateFile("cover.png", bytes);
    _fixture.CreateFile("cover (1).png", bytes);

    var result = await Discover().DiscoverAsync(_fixture.Root, [], CancellationToken.None);

    Assert.Single(result.Candidates);
    Assert.Equal("cover.png", Path.GetFileName(result.Selected!.SourcePath));
  }

  [Fact]
  public async Task DiscoverAsync_selects_a_deterministic_representative_for_identical_embedded_payload()
  {
    var bytes = TinyPng(64, 64);
    _fixture.CreateFile("other.png", bytes);

    var result = await Discover(bytes).DiscoverAsync(_fixture.Root, [Source("01.mp3", Picture(0, "Cover"))], CancellationToken.None);

    var candidate = Assert.Single(result.Candidates);
    Assert.Equal(CoverOrigin.EmbeddedPicture, candidate.Origin);
  }

  [Fact]
  public async Task DiscoverAsync_keeps_lower_resolution_alternate_but_ranks_it_lower()
  {
    _fixture.CreateFile("small.png", TinyPng(10, 10));
    _fixture.CreateFile("large.png", TinyPng(20, 20));

    var result = await Discover().DiscoverAsync(_fixture.Root, [], CancellationToken.None);

    Assert.Equal((IEnumerable<string>)["large.png", "small.png"], result.Candidates.Select(candidate => Path.GetFileName(candidate.SourcePath)));
  }

  [Fact]
  public async Task DiscoverAsync_prior_content_hash_wins_after_path_spelling_changes()
  {
    var priorBytes = TinyPng(10, 10);
    var priorHash = Hash(priorBytes);
    _fixture.CreateFile("renamed.PNG", priorBytes);
    _fixture.CreateFile("cover.jpg", TinyJpeg(100, 100));

    var result = await Discover().DiscoverAsync(_fixture.Root, [], new CoverDiscoveryOptions(priorHash), CancellationToken.None);

    Assert.Equal(priorHash, result.SelectedContentHash);
    Assert.Equal("renamed.PNG", Path.GetFileName(result.Selected!.SourcePath));
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task DiscoverAsync_reads_valid_png_and_jpeg_dimensions(bool png)
  {
    _fixture.CreateFile(png ? "cover.png" : "cover.jpg", png ? TinyPng(123, 456) : TinyJpeg(123, 456));

    var candidate = Assert.Single((await Discover().DiscoverAsync(_fixture.Root, [], CancellationToken.None)).Candidates);

    Assert.Equal(123, candidate.Width);
    Assert.Equal(456, candidate.Height);
  }

  [Fact]
  public async Task DiscoverAsync_rejects_corrupt_and_truncated_images()
  {
    _fixture.CreateFile("bad.png", [137, 80, 78]);

    var result = await Discover().DiscoverAsync(_fixture.Root, [], CancellationToken.None);

    var rejection = Assert.Single(result.Rejections);
    Assert.Equal("cover.invalid-header", rejection.Code);
    Assert.Empty(result.Candidates);
  }

  [Fact]
  public async Task DiscoverAsync_does_not_discover_unsupported_extensions()
  {
    _fixture.CreateFile("cover.gif", [1, 2, 3]);

    var result = await Discover().DiscoverAsync(_fixture.Root, [], CancellationToken.None);

    Assert.Empty(result.Candidates);
    Assert.Empty(result.Rejections);
  }

  [Fact]
  public async Task DiscoverAsync_rejects_excessive_dimensions_and_encoded_size()
  {
    _fixture.CreateFile("huge.png", TinyPng(16385, 1));
    _fixture.CreateFile("large.jpg", TinyJpeg(1, 1), CoverDiscoverer.MaximumEncodedPayloadBytes + 1);

    var result = await Discover().DiscoverAsync(_fixture.Root, [], CancellationToken.None);

    Assert.Equal((IEnumerable<string>)["cover.dimension-too-large", "cover.payload-too-large"], result.Rejections.Select(rejection => rejection.Code).OrderBy(code => code, StringComparer.Ordinal));
  }

  [Fact]
  public async Task DiscoverAsync_turns_unreadable_payload_into_a_structured_rejection()
  {
    var result = await new CoverDiscoverer(new ThrowingPayloadOpener()).DiscoverAsync(_fixture.Root, [Source("01.mp3", Picture(0, "Cover"))], CancellationToken.None);

    Assert.Equal("cover.payload-unreadable", Assert.Single(result.Rejections).Code);
  }

  [Fact]
  public async Task DiscoverAsync_propagates_cancellation_during_payload_read()
  {
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Discover().DiscoverAsync(_fixture.Root, [], cancellation.Token));
  }

  [Fact]
  public async Task DiscoverAsync_succeeds_without_art()
  {
    var result = await Discover().DiscoverAsync(_fixture.Root, [], CancellationToken.None);

    Assert.Empty(result.Candidates);
    Assert.Empty(result.Rejections);
    Assert.Null(result.Selected);
    Assert.Null(result.SelectedContentHash);
  }

  private static CoverDiscoverer Discover(byte[]? embeddedBytes = null) => new(new TestPayloadOpener(embeddedBytes));

  private SourceFile Source(string path, MediaAttachedPicture picture) => new(Path.Combine(_fixture.Root, path), path, new([], [picture], [], new TagCollection([]), new Dictionary<string, string>(), [], 1));
  private static MediaAttachedPicture Picture(int index, string title) => new(index, "mjpeg", null, null, new Dictionary<string, string> { ["title"] = title });
  private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
  private static byte[] TinyPng(int width, int height) => [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width, (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, 8, 6, 0, 0, 0];
  private static byte[] TinyJpeg(int width, int height) => [255, 216, 255, 192, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 17, 0, 2, 17, 0, 3, 17, 0, 255, 217];

  public void Dispose() => _fixture.Dispose();

  private sealed class TestPayloadOpener(byte[]? embeddedBytes) : ICoverPayloadOpener
  {
    public ValueTask<Stream> OpenReadAsync(CoverPayloadReference payload, CancellationToken cancellationToken)
      => ValueTask.FromResult<Stream>(payload.Origin == CoverOrigin.EmbeddedPicture ? new MemoryStream(embeddedBytes ?? []) : File.OpenRead(payload.SourcePath));
  }
  private sealed class ThrowingPayloadOpener : ICoverPayloadOpener
  {
    public ValueTask<Stream> OpenReadAsync(CoverPayloadReference payload, CancellationToken cancellationToken) => throw new IOException("denied");
  }
  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory() { Root = Path.Combine(Path.GetTempPath(), $"AudiobookConverter-{Guid.NewGuid():N}"); Directory.CreateDirectory(Root); }
    public string Root { get; }
    public string CreateFile(string relativePath, byte[] bytes, long? length = null)
    {
      var path = Path.Combine(Root, relativePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      using var stream = File.Create(path); stream.Write(bytes); if (length is { } target) stream.SetLength(target); return path;
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
  }
}
