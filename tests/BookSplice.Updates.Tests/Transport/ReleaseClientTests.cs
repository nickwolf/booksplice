using System.Net;
using System.Text;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Transport;

namespace BookSplice.Updates.Tests.Transport;

public sealed class ReleaseClientTests
{
  [Fact]
  public async Task MetadataChangesPreserveDistinctAssetIdentity()
  {
    var first = ReleaseSelector.Select(await Client(_ => Page(Release(7, "v1.0.0", 11, 100))).FetchAsync(CancellationToken.None));
    var identical = ReleaseSelector.Select(await Client(_ => Page(Release(7, "v1.0.0", 11, 100))).FetchAsync(CancellationToken.None));
    var changed = ReleaseSelector.Select(await Client(_ => Page(Release(7, "v1.0.0", 12, 101))).FetchAsync(CancellationToken.None));
    var renamed = ReleaseSelector.Select(await Client(_ => Page(ReleaseRaw(7, "v1.0.0", $"[{Asset(11, "renamed.zip", 100)}]"))).FetchAsync(CancellationToken.None));
    var relocated = ReleaseSelector.Select(await Client(_ => Page(ReleaseRaw(7, "v1.0.0", $"[{Asset(11, "BookSplice-1.0.0-win-x64.zip", 100).Replace("releases/download", "other/download", StringComparison.Ordinal)}]"))).FetchAsync(CancellationToken.None));
    var revisedNotes = ReleaseSelector.Select(await Client(_ => Page(Release(7, "v1.0.0", 11, 100).Replace("\"body\":\"notes\"", "\"body\":\"revised notes\"", StringComparison.Ordinal))).FetchAsync(CancellationToken.None));
    Assert.Equal(first.Identity, identical.Identity);
    Assert.NotEqual(first.Identity, changed.Identity);
    Assert.NotEqual(first.Identity, renamed.Identity);
    Assert.NotEqual(first.Identity, relocated.Identity);
    Assert.NotEqual(first.Identity, revisedNotes.Identity);
    Assert.NotEqual(first.Identity!.Assets[0], changed.Identity!.Assets[0]);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task BuildMetadataUrlsAcceptLiteralAndEscapedPlus(bool escaped)
  {
    const string tag = "v1.2.3+build.1";
    var version = tag[1..];
    var names = new[]
    {
      $"BookSplice-{version}-win-x64.zip",
      $"BookSplice-{version}-win-x64.zip.sha256",
      $"BookSplice-{version}-win-x64.update.json",
      $"BookSplice-{version}-win-x64.update.sig.json"
    };
    var urlTag = escaped ? Uri.EscapeDataString(tag) : tag;
    var assets = names.Select((name, index) =>
      $$"""{"id":{{index + 1}},"name":"{{name}}","size":100,"browser_download_url":"https://github.com/nickwolf/booksplice/releases/download/{{urlTag}}/{{(escaped ? Uri.EscapeDataString(name) : name)}}"}""");
    var release = ReleaseRaw(7, tag, $"[{string.Join(',', assets)}]")
      .Replace($"/releases/tag/{tag}", $"/releases/tag/{urlTag}", StringComparison.Ordinal);
    var client = Client(_ => Page(release));
    var selected = ReleaseSelector.Select(await client.FetchAsync(CancellationToken.None));
    Assert.Equal(SelectionKind.Available, selected.Kind);
    Assert.True(selected.Installable);
    using var transport = new ValidatedHttpTransport(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new ByteArrayContent([1])
    }), TimeProvider.System);
    using var response = await transport.OpenAssetAsync(selected.Candidate!.Assets[0], tag, CancellationToken.None);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
  }

  [Fact]
  public async Task EleventhPageIsExplicitFailure()
  {
    var client = Client(request =>
    {
      var page = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!, System.Globalization.CultureInfo.InvariantCulture);
      var response = Page();
      response.Headers.TryAddWithoutValidation("Link", $"<https://api.github.com/repos/nickwolf/booksplice/releases?per_page=100&page={page + 1}>; rel=\"next\"");
      return response;
    });
    await Assert.ThrowsAsync<ReleaseTransportException>(() => client.FetchAsync(CancellationToken.None));
  }

  [Fact]
  public async Task CrossHostPaginationIsRejected()
  {
    var client = Client(_ =>
    {
      var response = Page();
      response.Headers.TryAddWithoutValidation("Link", "<https://example.com/repos/nickwolf/booksplice/releases?per_page=100&page=2>; rel=\"next\"");
      return response;
    });
    await Assert.ThrowsAsync<ReleaseTransportException>(() => client.FetchAsync(CancellationToken.None));
  }

  [Fact]
  public async Task EquivalentPaginationQueryOrderIsAccepted()
  {
    Assert.True(ValidatedHttpTransport.SameApiPage(
      new Uri("https://api.github.com/repos/nickwolf/booksplice/releases?page=2&per_page=100"),
      new Uri("https://api.github.com/repos/nickwolf/booksplice/releases?per_page=100&page=2")));
    var client = Client(request =>
    {
      var response = Page();
      if (System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"] == "1")
      {
        response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/repos/nickwolf/booksplice/releases?page=2&per_page=100>; rel=\"next\"");
      }

      return response;
    });
    Assert.Empty(await client.FetchAsync(CancellationToken.None));
  }

  [Fact]
  public async Task CrossHostApiRedirectIsRejected()
  {
    var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/") } });
    await Assert.ThrowsAsync<ReleaseTransportException>(() => client.FetchAsync(CancellationToken.None));
  }

  [Fact]
  public async Task SameHostApiRedirectCannotSkipPages()
  {
    var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
    {
      Headers = { Location = new Uri("https://api.github.com/repos/nickwolf/booksplice/releases?per_page=100&page=2") }
    });
    await Assert.ThrowsAsync<ReleaseTransportException>(() => client.FetchAsync(CancellationToken.None));
  }

  [Fact]
  public async Task RateLimitResetIsReported()
  {
    var reset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
    var client = Client(_ =>
    {
      var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
      response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToString(System.Globalization.CultureInfo.InvariantCulture));
      return response;
    });
    var error = await Assert.ThrowsAsync<ReleaseTransportException>(() => client.FetchAsync(CancellationToken.None));
    Assert.Equal(reset, error.RetryAfter?.ToUnixTimeSeconds());
  }

  [Fact]
  public async Task AssetRedirectRejectsUnlistedHost()
  {
    using var transport = new ValidatedHttpTransport(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
    {
      Headers = { Location = new Uri("https://example.com/package.zip") }
    }), TimeProvider.System);
    var asset = new ReleaseAsset(1, "BookSplice-1.0.0-win-x64.zip", new Uri("https://github.com/nickwolf/booksplice/releases/download/v1.0.0/BookSplice-1.0.0-win-x64.zip"), 100);
    await Assert.ThrowsAsync<ReleaseTransportException>(() => transport.OpenAssetAsync(asset, "v1.0.0", CancellationToken.None));
  }

  [Fact]
  public async Task AssetRedirectAcceptsListedHost()
  {
    var calls = 0;
    using var transport = new ValidatedHttpTransport(new FakeHandler(_ =>
    {
      calls++;
      return calls == 1 ? new HttpResponseMessage(HttpStatusCode.Redirect)
      {
        Headers = { Location = new Uri("https://release-assets.githubusercontent.com/opaque") }
      } : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
    }), TimeProvider.System);
    var asset = new ReleaseAsset(1, "BookSplice-1.0.0-win-x64.zip", new Uri("https://github.com/nickwolf/booksplice/releases/download/v1.0.0/BookSplice-1.0.0-win-x64.zip"), 100);
    using var response = await transport.OpenAssetAsync(asset, "v1.0.0", CancellationToken.None);
    Assert.Equal(2, calls);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
  }

  [Fact]
  public async Task DuplicateAssetsAreRejected()
  {
    var asset = Asset(11, "BookSplice-1.0.0-win-x64.zip", 100);
    var client = Client(_ => Page(ReleaseRaw(7, "v1.0.0", $"[{asset},{asset}]")));
    await Assert.ThrowsAsync<ReleaseTransportException>(() => client.FetchAsync(CancellationToken.None));
  }

  [Theory]
  [InlineData("\"id\":9223372036854775808", "\"id\":7")]
  [InlineData("\"html_url\":null", "\"html_url\":\"https://github.com/nickwolf/booksplice/releases/tag/v1.0.0\"")]
  public async Task MalformedMetadataBecomesTypedFailure(string replacement, string original)
  {
    var body = Release(7, "v1.0.0", 11, 100).Replace(original, replacement, StringComparison.Ordinal);
    var client = Client(_ => Page(body));
    await Assert.ThrowsAsync<ReleaseTransportException>(() => client.FetchAsync(CancellationToken.None));
  }

  [Fact]
  public async Task OversizedPageIsRejectedBeforeParsing()
  {
    var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[UpdateLimits.ReleasePageMaxBytes + 1]) });
    await Assert.ThrowsAsync<ReleaseTransportException>(() => client.FetchAsync(CancellationToken.None));
  }

  [Fact]
  public async Task LargeMalformedAngleNotesAreBoundedWithoutRegexBacktracking()
  {
    var notes = new string('<', 262144);
    var release = Release(7, "v1.0.0", 11, 100).Replace("\"body\":\"notes\"", $"\"body\":{System.Text.Json.JsonSerializer.Serialize(notes)}", StringComparison.Ordinal);
    var client = Client(_ => Page(release));
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var result = await client.FetchAsync(CancellationToken.None);
    watch.Stop();
    Assert.Equal(new string('<', (int)UpdateLimits.NotesMaxBytes), result[0].Notes);
    Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), $"Notes processing took {watch.Elapsed}.");
  }

  [Fact]
  public async Task NotesDropCompleteHtmlTagsAndDecodeEntitiesAsText()
  {
    var release = Release(7, "v1.0.0", 11, 100).Replace("\"body\":\"notes\"", "\"body\":\"<b>Release</b> &amp; notes\"", StringComparison.Ordinal);
    var result = await Client(_ => Page(release)).FetchAsync(CancellationToken.None);
    Assert.Equal("Release & notes", result[0].Notes);
  }

  [Fact]
  public async Task EncodedSlashCannotChangeBoundReleaseTag()
  {
    var release = Release(7, "v1.0.0", 11, 100).Replace("/releases/tag/v1.0.0", "/releases/tag/v1.0.0%2Fother", StringComparison.Ordinal);
    await Assert.ThrowsAsync<ReleaseTransportException>(() => Client(_ => Page(release)).FetchAsync(CancellationToken.None));
  }

  [Fact]
  public async Task InterruptedBodyBecomesTypedTransportFailure()
  {
    var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) });
    await Assert.ThrowsAsync<ReleaseTransportException>(() => client.FetchAsync(CancellationToken.None));
  }

  private static GitHubReleaseClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) => new(new FakeHandler(responder), TimeProvider.System);

  private static HttpResponseMessage Page(params string[] releases) => new(HttpStatusCode.OK) { Content = new StringContent($"[{string.Join(',', releases)}]", Encoding.UTF8, "application/json") };

  private static string Release(long id, string tag, long assetId, long length) => ReleaseRaw(id, tag, $"[{Asset(assetId, "BookSplice-1.0.0-win-x64.zip", length)}]");

  private static string ReleaseRaw(long id, string tag, string assets) => $$"""{"id":{{id}},"tag_name":"{{tag}}","html_url":"https://github.com/nickwolf/booksplice/releases/tag/{{tag}}","body":"notes","draft":false,"prerelease":false,"assets":{{assets}}}""";

  private static string Asset(long id, string name, long length) => $$"""{"id":{{id}},"name":"{{name}}","size":{{length}},"browser_download_url":"https://github.com/nickwolf/booksplice/releases/download/v1.0.0/{{name}}"}""";

  private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(responder(request));
  }

  private sealed class BrokenStream : MemoryStream
  {
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
      ValueTask.FromException<int>(new IOException("interrupted"));
  }
}
