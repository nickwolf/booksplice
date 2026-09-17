using System.Security.Cryptography;
using System.Diagnostics;
using BookSplice.Core.Analysis;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;

namespace BookSplice.Core.Tests.Covers;

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
  public async Task DiscoverAsync_rejects_a_png_without_iend()
  {
    var png = TinyPng(10, 10);
    _fixture.CreateFile("truncated.png", png[..^12]);

    var result = await Discover().DiscoverAsync(_fixture.Root, [], CancellationToken.None);

    Assert.Equal("cover.invalid-header", Assert.Single(result.Rejections).Code);
  }

  [Fact]
  public async Task DiscoverAsync_accepts_a_png_with_a_large_late_chunk()
  {
    var png = LargePngChunk(123, 456, 128 * 1024);
    _fixture.CreateFile("large.png", png);

    var candidate = Assert.Single((await Discover().DiscoverAsync(_fixture.Root, [], CancellationToken.None)).Candidates);

    Assert.Equal("image/png", candidate.ContentType);
    Assert.Equal(123, candidate.Width);
    Assert.Equal(456, candidate.Height);
  }

  [Fact]
  public async Task DiscoverAsync_rejects_a_truncated_large_png_chunk()
  {
    var png = LargePngChunk(123, 456, 128 * 1024);
    _fixture.CreateFile("truncated-large.png", png[..^1]);

    var result = await Discover().DiscoverAsync(_fixture.Root, [], CancellationToken.None);

    Assert.Equal("cover.invalid-header", Assert.Single(result.Rejections).Code);
  }

  [Fact]
  public async Task DiscoverAsync_rejects_a_jpeg_without_eoi()
  {
    _fixture.CreateFile("truncated.jpg", TinyJpeg(10, 10)[..^2]);

    var result = await Discover().DiscoverAsync(_fixture.Root, [], CancellationToken.None);

    Assert.Equal("cover.invalid-header", Assert.Single(result.Rejections).Code);
  }

  [Fact]
  public async Task DiscoverAsync_skips_a_reparse_point_source_root()
  {
    using var external = new TemporaryDirectory();
    external.CreateFile("cover.png", TinyPng(10, 10));
    var link = Path.Combine(_fixture.Root, "linked-root");
    try
    {
      await CreateJunctionAsync(link, external.Root);
      var result = await Discover().DiscoverAsync(link, [], CancellationToken.None);
      Assert.Empty(result.Candidates);
      Assert.Equal("cover.reparse-point-skipped", Assert.Single(result.Rejections).Code);
    }
    finally { if (Directory.Exists(link)) Directory.Delete(link); }
  }

  [Fact]
  public async Task DiscoverAsync_rejects_a_reparse_point_payload()
  {
    using var external = new TemporaryDirectory();
    external.CreateFile("cover.png", TinyPng(10, 10));
    var link = Path.Combine(_fixture.Root, "linked");
    try
    {
      await CreateJunctionAsync(link, external.Root);
      var result = await new CoverDiscoverer().DiscoverAsync(_fixture.Root, [], CancellationToken.None);
      Assert.Equal("cover.reparse-point-skipped", Assert.Single(result.Rejections).Code);
    }
    finally { if (Directory.Exists(link)) Directory.Delete(link); }
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
  public async Task DiscoverAsync_observes_cancellation_while_enumerating_directory_entries()
  {
    using var cancellation = new CancellationTokenSource();
    var enumerator = new CancellingDirectoryEnumerator(cancellation);

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CoverDiscoverer(directoryEnumerator: enumerator).DiscoverAsync(_fixture.Root, [], cancellation.Token));
  }

  [Fact]
  public async Task DiscoverAsync_skips_an_out_of_root_directory_before_recursing()
  {
    using var outside = new TemporaryDirectory();
    outside.CreateFile("cover.png", TinyPng(10, 10));
    var enumerator = new OutOfRootDirectoryEnumerator(_fixture.Root, outside.Root);

    var result = await new CoverDiscoverer(directoryEnumerator: enumerator).DiscoverAsync(_fixture.Root, [], CancellationToken.None);

    Assert.Empty(result.Candidates);
    Assert.Equal("cover.path-outside-root", Assert.Single(result.Rejections).Code);
  }

  [Fact]
  public async Task DiscoverAsync_propagates_cancellation_from_an_in_progress_read()
  {
    using var cancellation = new CancellationTokenSource();
    var discoverer = new CoverDiscoverer(new CancellingPayloadOpener(cancellation));

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discoverer.DiscoverAsync(_fixture.Root, [Source("01.mp3", Picture(0, "Cover"))], cancellation.Token));
  }

  [Fact]
  public async Task DiscoverAsync_rejects_decoded_memory_above_the_limit()
  {
    _fixture.CreateFile("memory.png", TinyPng(10_000, 10_000));

    var result = await Discover().DiscoverAsync(_fixture.Root, [], CancellationToken.None);

    Assert.Equal("cover.decoded-memory-too-large", Assert.Single(result.Rejections).Code);
  }

  [Fact]
  public async Task DiscoverAsync_does_not_treat_frontline_as_front_cover()
  {
    var result = await Discover(TinyPng(10, 10)).DiscoverAsync(_fixture.Root, [Source("01.mp3", Picture(0, "frontline recording"))], CancellationToken.None);

    Assert.Equal(CoverSemanticType.Other, Assert.Single(result.Candidates).SemanticType);
  }

  [Fact]
  public async Task DiscoverAsync_is_deterministic_when_probed_files_arrive_in_a_different_order()
  {
    var bytes = TinyPng(10, 10);
    var first = await Discover(bytes).DiscoverAsync(_fixture.Root, [Source("02.mp3", Picture(0, "Cover")), Source("01.mp3", Picture(0, "Cover"))], CancellationToken.None);
    var second = await Discover(bytes).DiscoverAsync(_fixture.Root, [Source("01.mp3", Picture(0, "Cover")), Source("02.mp3", Picture(0, "Cover"))], CancellationToken.None);

    Assert.Equal(first.Selected!.SourceIdentity, second.Selected!.SourceIdentity);
  }

  [Fact]
  public async Task DiscoverAsync_exposes_the_options_overload_through_the_interface()
  {
    var bytes = TinyPng(10, 10);
    var hash = Hash(bytes);
    _fixture.CreateFile("art.png", bytes);
    var result = await DiscoverViaInterface(Discover(), _fixture.Root, new CoverDiscoveryOptions(hash));

    Assert.Equal(hash, result.SelectedContentHash);
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
#pragma warning disable CA1859 // The test verifies the public interface contract.
  private static Task<CoverDiscoveryResult> DiscoverViaInterface(ICoverDiscoverer discoverer, string root, CoverDiscoveryOptions options)
    => discoverer.DiscoverAsync(root, [], options, CancellationToken.None);
#pragma warning restore CA1859

  private SourceFile Source(string path, MediaAttachedPicture picture) => new(Path.Combine(_fixture.Root, path), path, new([], [picture], [], new TagCollection([]), new Dictionary<string, string>(), [], 1));
  private static MediaAttachedPicture Picture(int index, string title) => new(index, "mjpeg", null, null, new Dictionary<string, string> { ["title"] = title });
  private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
  private static byte[] TinyPng(int width, int height)
  {
    var value = new List<byte>([137, 80, 78, 71, 13, 10, 26, 10]);
    value.AddRange(PngChunk("IHDR", [(byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width, (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, 8, 6, 0, 0, 0]));
    value.AddRange(PngChunk("IDAT", [])); value.AddRange(PngChunk("IEND", []));
    return value.ToArray();
  }
  private static byte[] LargePngChunk(int width, int height, int dataLength)
  {
    var value = new List<byte>(TinyPng(width, height).Length + dataLength + 12);
    value.AddRange([137, 80, 78, 71, 13, 10, 26, 10]);
    value.AddRange(PngChunk("IHDR", [(byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width, (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, 8, 6, 0, 0, 0]));
    value.AddRange(PngChunk("IDAT", new byte[dataLength]));
    value.AddRange(PngChunk("IEND", []));
    return value.ToArray();
  }
  private static byte[] TinyJpeg(int width, int height) => [255, 216, 255, 192, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 17, 0, 2, 17, 0, 3, 17, 0, 255, 217];

  private static byte[] PngChunk(string type, byte[] data)
  {
    var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
    var value = new byte[12 + data.Length];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(value, (uint)data.Length);
    typeBytes.CopyTo(value, 4); data.CopyTo(value, 8);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(8 + data.Length), Crc32(value.AsSpan(4, data.Length + 4)));
    return value;
  }
  private static uint Crc32(ReadOnlySpan<byte> value)
  {
    var crc = 0xffffffffU;
    foreach (var item in value) { crc ^= item; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0xedb88320U); }
    return ~crc;
  }

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
  private sealed class CancellingPayloadOpener(CancellationTokenSource cancellation) : ICoverPayloadOpener
  {
    public ValueTask<Stream> OpenReadAsync(CoverPayloadReference payload, CancellationToken cancellationToken) => ValueTask.FromResult<Stream>(new CancellingStream(cancellation));
  }
  private sealed class CancellingDirectoryEnumerator(CancellationTokenSource cancellation) : ICoverDirectoryEnumerator
  {
    public IEnumerable<string> EnumerateFileSystemEntries(string directory)
    {
      cancellation.Cancel();
      yield return Path.Combine(directory, "cover.png");
    }
  }
  private sealed class OutOfRootDirectoryEnumerator(string sourceRoot, string outsideRoot) : ICoverDirectoryEnumerator
  {
    public IEnumerable<string> EnumerateFileSystemEntries(string directory)
      => string.Equals(directory, sourceRoot, StringComparison.OrdinalIgnoreCase)
        ? [outsideRoot]
        : [Path.Combine(outsideRoot, "cover.png")];
  }
  private sealed class CancellingStream(CancellationTokenSource cancellation) : MemoryStream(TinyPng(10, 10))
  {
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
      cancellation.Cancel();
      return base.ReadAsync(buffer, cancellationToken);
    }
  }
  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory() { Root = Path.Combine(Path.GetTempPath(), $"BookSplice-{Guid.NewGuid():N}"); Directory.CreateDirectory(Root); }
    public string Root { get; }
    public string CreateFile(string relativePath, byte[] bytes, long? length = null)
    {
      var path = Path.Combine(Root, relativePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      using var stream = File.Create(path); stream.Write(bytes); if (length is { } target) stream.SetLength(target); return path;
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
  }
  private static async Task CreateJunctionAsync(string linkPath, string targetPath)
  {
    using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"") { CreateNoWindow = true, RedirectStandardError = true, UseShellExecute = false })!;
    await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
  }
}
