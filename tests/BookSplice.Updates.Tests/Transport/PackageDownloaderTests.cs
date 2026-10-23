using System.Net;
using System.Security.Cryptography;
using System.Text;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Security;
using BookSplice.Updates.Transport;

namespace BookSplice.Updates.Tests.Transport;

public sealed class PackageDownloaderTests
{
  [Fact]
  public async Task ExactLengthAndHashAreRequired()
  {
    var fixture = new Fixture();
    using var transport = Fixture.Transport(request => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
        ? fixture.Checksum : fixture.Data)
    });
    using var output = new MemoryStream();
    var downloader = new PackageDownloader(new FixedClient(fixture.Release), transport);
    await downloader.DownloadAsync(fixture.Release, fixture.Manifest, output, null, CancellationToken.None);
    Assert.Equal(fixture.Data, output.ToArray());
    Assert.True(output.CanWrite);
  }

  [Fact]
  public async Task ReleaseChangedAfterConfirmationFailsBeforePayloadExecution()
  {
    var fixture = new Fixture();
    var assets = fixture.Release.Assets.ToArray();
    assets[0] = assets[0] with { Id = 99 };
    var changed = fixture.Release with { Assets = assets };
    var requests = 0;
    using var transport = Fixture.Transport(_ =>
    {
      requests++;
      return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Data) };
    });
    using var output = new MemoryStream();
    await Assert.ThrowsAsync<PackageDownloadException>(() =>
      new PackageDownloader(new FixedClient(changed), transport).DownloadAsync(fixture.Release, fixture.Manifest,
        output, null, CancellationToken.None));
    Assert.Equal(0, requests);
    Assert.Empty(output.ToArray());
  }

  [Fact]
  public async Task ManifestCannotAuthorizeAnotherConfirmationWithSameTag()
  {
    var fixture = new Fixture();
    var changed = fixture.Release with { Notes = "new confirmation" };
    var requests = 0;
    using var transport = Fixture.Transport(_ =>
    {
      requests++;
      return new HttpResponseMessage(HttpStatusCode.OK);
    });
    using var output = new MemoryStream();
    await Assert.ThrowsAsync<PackageDownloadException>(() =>
      new PackageDownloader(new FixedClient(changed), transport).DownloadAsync(changed, fixture.Manifest,
        output, null, CancellationToken.None));
    Assert.Equal(0, requests);
  }

  [Theory]
  [InlineData(2)]
  [InlineData(4)]
  public async Task ShortOrLongStreamFails(int length)
  {
    var fixture = new Fixture();
    using var transport = Fixture.Transport(request => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
        ? fixture.Checksum : Enumerable.Repeat((byte)'a', length).ToArray())
    });
    using var output = new MemoryStream();
    await Assert.ThrowsAsync<PackageDownloadException>(() =>
      new PackageDownloader(new FixedClient(fixture.Release), transport).DownloadAsync(fixture.Release,
        fixture.Manifest, output, null, CancellationToken.None));
  }

  [Fact]
  public async Task SameLengthWithWrongHashFails()
  {
    var fixture = new Fixture();
    using var transport = Fixture.Transport(request => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
        ? fixture.Checksum : Encoding.ASCII.GetBytes("abd"))
    });
    using var output = new MemoryStream();
    await Assert.ThrowsAsync<PackageDownloadException>(() =>
      new PackageDownloader(new FixedClient(fixture.Release), transport).DownloadAsync(fixture.Release,
        fixture.Manifest, output, null, CancellationToken.None));
  }

  [Fact]
  public async Task CompanionMustAgreeWithSignedHash()
  {
    var fixture = new Fixture();
    var payloadRequests = 0;
    using var transport = Fixture.Transport(request =>
    {
      if (!request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
      {
        payloadRequests++;
      }

      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new ByteArrayContent(Encoding.ASCII.GetBytes(new string('0', 64) +
          "  BookSplice-1.2.3-win-x64.zip\n"))
      };
    });
    using var output = new MemoryStream();
    await Assert.ThrowsAsync<PackageDownloadException>(() =>
      new PackageDownloader(new FixedClient(fixture.Release), transport).DownloadAsync(fixture.Release,
        fixture.Manifest, output, null, CancellationToken.None));
    Assert.Equal(0, payloadRequests);
  }

  [Fact]
  public async Task InterruptedReadIsTypedFailure()
  {
    var fixture = new Fixture();
    using var transport = Fixture.Transport(request => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
        ? new ByteArrayContent(fixture.Checksum) : new StreamContent(new InterruptedStream())
    });
    using var output = new MemoryStream();
    await Assert.ThrowsAsync<PackageDownloadException>(() =>
      new PackageDownloader(new FixedClient(fixture.Release), transport).DownloadAsync(fixture.Release,
        fixture.Manifest, output, null, CancellationToken.None));
  }

  [Fact]
  public async Task IdleDeadlineAndCallerCancellationAreDistinct()
  {
    var fixture = new Fixture();
    using var transport = Fixture.Transport(request => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
        ? new ByteArrayContent(fixture.Checksum) : new StreamContent(new DelayedStream())
    });
    using var output = new MemoryStream();
    var downloader = new PackageDownloader(new FixedClient(fixture.Release), transport,
      TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(5));
    var timeout = await Assert.ThrowsAsync<PackageDownloadException>(() =>
      downloader.DownloadAsync(fixture.Release, fixture.Manifest, output, null, CancellationToken.None));
    Assert.Contains("idle", timeout.Message, StringComparison.Ordinal);

    using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
    var cancelledDownloader = new PackageDownloader(new FixedClient(fixture.Release), transport,
      TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
      cancelledDownloader.DownloadAsync(fixture.Release, fixture.Manifest, output, null, cancelled.Token));
  }

  [Fact]
  public async Task StalledAssetHeadersHitIdleDeadline()
  {
    var fixture = new Fixture();
    using var transport = Fixture.TransportAsync(async (_, token) =>
    {
      await Task.Delay(Timeout.InfiniteTimeSpan, token);
      return new HttpResponseMessage(HttpStatusCode.OK);
    });
    using var output = new MemoryStream();
    var failure = await Assert.ThrowsAsync<PackageDownloadException>(() =>
      new PackageDownloader(new FixedClient(fixture.Release), transport,
        TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(5)).DownloadAsync(fixture.Release,
        fixture.Manifest, output, null, CancellationToken.None));
    Assert.Contains("idle", failure.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task StalledStreamCreationHitsIdleDeadline()
  {
    var fixture = new Fixture();
    using var transport = Fixture.Transport(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StalledContent()
    });
    using var output = new MemoryStream();
    var failure = await Assert.ThrowsAsync<PackageDownloadException>(() =>
      new PackageDownloader(new FixedClient(fixture.Release), transport,
        TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(5)).DownloadAsync(fixture.Release,
        fixture.Manifest, output, null, CancellationToken.None));
    Assert.Contains("idle", failure.Message, StringComparison.Ordinal);
  }

  private sealed class Fixture
  {
    public byte[] Data { get; } = Encoding.ASCII.GetBytes("abc");
    public ReleaseCandidate Release { get; }
    public VerifiedManifest Manifest { get; }
    public byte[] Checksum { get; }

    public Fixture()
    {
      const string name = "BookSplice-1.2.3-win-x64.zip";
      var tag = "v1.2.3";
      Release = new ReleaseCandidate(1, tag, SemanticVersion.ParseTag(tag),
        new Uri($"https://github.com/nickwolf/booksplice/releases/tag/{tag}"), "notes", false, false,
        [Asset(1, name, 3), Asset(2, name + ".sha256", 85),
         Asset(3, "BookSplice-1.2.3-win-x64.update.json", 1),
         Asset(4, "BookSplice-1.2.3-win-x64.update.sig.json", 1)]);
      var hash = Convert.ToHexStringLower(SHA256.HashData(Data));
      Checksum = Encoding.ASCII.GetBytes($"{hash}  {name}\n");
      Manifest = new VerifiedManifest(1, "BookSplice", "nickwolf/booksplice", tag, "1.2.3", "win-x64",
        name, 3, hash, 1, 1, 1, ReleaseSelector.Select([Release]).Identity!);
    }

    public static ValidatedHttpTransport Transport(Func<HttpRequestMessage, HttpResponseMessage> response) =>
      new(new FakeHandler(response), TimeProvider.System);

    public static ValidatedHttpTransport TransportAsync(
      Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
      new(new AsyncHandler(response), TimeProvider.System);

    private static ReleaseAsset Asset(long id, string name, long size) =>
      new(id, name, new Uri($"https://github.com/nickwolf/booksplice/releases/download/v1.2.3/{name}"), size);
  }

  private sealed class FixedClient(ReleaseCandidate candidate) : IReleaseClient
  {
    public Task<IReadOnlyList<ReleaseCandidate>> FetchAsync(CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<ReleaseCandidate>>([candidate]);
  }

  private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
      Task.FromResult(response(request));
  }

  private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
      response(request, cancellationToken);
  }

  private sealed class StalledContent : HttpContent
  {
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
      Task.CompletedTask;

    protected override bool TryComputeLength(out long length)
    {
      length = 0;
      return false;
    }

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      return Stream.Null;
    }
  }

  private sealed class InterruptedStream : MemoryStream
  {
    public override bool CanSeek => false;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
      ValueTask.FromException<int>(new IOException("interrupted"));
  }

  private sealed class DelayedStream : MemoryStream
  {
    public override bool CanSeek => false;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      return 0;
    }
  }
}
