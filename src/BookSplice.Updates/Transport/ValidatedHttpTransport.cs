using System.Net;
using BookSplice.Updates.Policy;

namespace BookSplice.Updates.Transport;

public sealed class ReleaseTransportException(string message, DateTimeOffset? retryAfter = null) : Exception(message)
{
  public DateTimeOffset? RetryAfter { get; } = retryAfter;
}

public sealed class ValidatedHttpTransport : IDisposable
{
  private readonly HttpClient _client;
  private readonly TimeProvider _clock;

  public ValidatedHttpTransport(HttpMessageHandler handler, TimeProvider clock)
  {
    _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    _clock = clock;
  }

  public static ValidatedHttpTransport CreateProduction(TimeProvider clock) =>
    new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, clock);

  public async Task<(byte[] Body, string? Link)> GetReleasePageAsync(Uri uri, CancellationToken cancellationToken)
  {
    var initialUri = uri;
    for (var redirect = 0; redirect < 4; redirect++)
    {
      if (!SameApiPage(uri, initialUri))
      {
        throw new ReleaseTransportException("Release API URL is outside the fixed repository endpoint.");
      }

      using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      deadline.CancelAfter(UpdateLimits.ApiRequestTimeout);
      using var request = new HttpRequestMessage(HttpMethod.Get, uri);
      request.Headers.UserAgent.ParseAdd("BookSplice-Updater/1");
      request.Headers.Accept.ParseAdd("application/vnd.github+json");
      HttpResponseMessage response;
      try
      {
        response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
        throw new ReleaseTransportException("Release API request timed out.");
      }
      catch (HttpRequestException exception)
      {
        throw new ReleaseTransportException($"Release API request failed: {exception.Message}");
      }

      using (response)
      {
        if (IsRedirect(response.StatusCode))
        {
          uri = response.Headers.Location is { IsAbsoluteUri: true } location ? location :
            throw new ReleaseTransportException("Release API redirect has no absolute location.");
          continue;
        }

        if (!response.IsSuccessStatusCode)
        {
          throw new ReleaseTransportException($"Release API returned HTTP {(int)response.StatusCode}.", RateLimitDeadline(response));
        }

        if (response.Content.Headers.ContentLength > UpdateLimits.ReleasePageMaxBytes)
        {
          throw new ReleaseTransportException("Release API page exceeds the size limit.");
        }

        try
        {
          await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
          using var output = new MemoryStream();
          var buffer = new byte[8192];
          while (true)
          {
            var count = await stream.ReadAsync(buffer, deadline.Token);
            if (count == 0)
            {
              break;
            }

            if (output.Length + count > UpdateLimits.ReleasePageMaxBytes)
            {
              throw new ReleaseTransportException("Release API page exceeds the size limit.");
            }

            output.Write(buffer, 0, count);
          }

          return (output.ToArray(), response.Headers.TryGetValues("Link", out var links) ? string.Join(',', links) : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
          throw new ReleaseTransportException("Release API response timed out.");
        }
        catch (IOException exception)
        {
          throw new ReleaseTransportException($"Release API response was interrupted: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
          throw new ReleaseTransportException($"Release API response failed: {exception.Message}");
        }
      }
    }

    throw new ReleaseTransportException("Release API redirected too many times.");
  }

  public async Task<HttpResponseMessage> OpenAssetAsync(ReleaseAsset asset, string tag, CancellationToken cancellationToken)
  {
    var expectedPath = $"/nickwolf/booksplice/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset.Name)}";
    var uri = asset.DownloadUri;
    if (!ValidGithubUri(uri) || uri.AbsolutePath != expectedPath)
    {
      throw new ReleaseTransportException("Asset URL does not match the selected repository release.");
    }

    for (var redirect = 0; redirect < 4; redirect++)
    {
      using var request = new HttpRequestMessage(HttpMethod.Get, uri);
      request.Headers.UserAgent.ParseAdd("BookSplice-Updater/1");
      HttpResponseMessage response;
      try
      {
        response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
      }
      catch (HttpRequestException exception)
      {
        throw new ReleaseTransportException($"Release asset request failed: {exception.Message}");
      }

      if (IsRedirect(response.StatusCode))
      {
        var location = response.Headers.Location;
        response.Dispose();
        if (location is null || !location.IsAbsoluteUri || !ValidAssetDeliveryUri(location))
        {
          throw new ReleaseTransportException("Release asset redirected outside GitHub asset delivery.");
        }

        uri = location;
        continue;
      }

      if (!response.IsSuccessStatusCode)
      {
        var status = response.StatusCode;
        response.Dispose();
        throw new ReleaseTransportException($"Release asset returned HTTP {(int)status}.");
      }

      if (response.Content.Headers.ContentLength > UpdateLimits.ZipMaxBytes)
      {
        response.Dispose();
        throw new ReleaseTransportException("Release asset exceeds the ZIP size limit.");
      }

      return response;
    }

    throw new ReleaseTransportException("Release asset redirected too many times.");
  }

  private static bool ValidGithubUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
    uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

  private static bool ValidAssetDeliveryUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
    uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
    uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

  public static bool ValidApiUri(Uri uri)
  {
    if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "api.github.com" ||
        !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
        uri.AbsolutePath != "/repos/nickwolf/booksplice/releases")
    {
      return false;
    }

    var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
    return query.Length == 2 && query.Count(part => part == "per_page=100") == 1 &&
      query.Count(part => part.StartsWith("page=", StringComparison.Ordinal)) == 1 &&
      int.TryParse(query.Single(part => part.StartsWith("page=", StringComparison.Ordinal))[5..],
        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var page) &&
      page is >= 1 and <= UpdateLimits.MaxReleasePages;
  }

  public static bool SameApiPage(Uri left, Uri right) => ValidApiUri(left) && ValidApiUri(right) &&
    PageNumber(left) == PageNumber(right);

  private static int PageNumber(Uri uri) => int.Parse(uri.Query.TrimStart('?').Split('&').Single(part => part.StartsWith("page=", StringComparison.Ordinal))[5..],
    System.Globalization.CultureInfo.InvariantCulture);

  private DateTimeOffset? RateLimitDeadline(HttpResponseMessage response)
  {
    if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
    {
      return null;
    }

    if (response.Headers.RetryAfter?.Date is { } date && date > _clock.GetUtcNow())
    {
      return date;
    }

    if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
    {
      return _clock.GetUtcNow() + delta;
    }

    if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
        long.TryParse(values.FirstOrDefault(), out var epoch))
    {
      try
      {
        var reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
        return reset > _clock.GetUtcNow() ? reset : null;
      }
      catch (ArgumentOutOfRangeException)
      {
      }
    }

    return null;
  }

  private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

  public void Dispose() => _client.Dispose();
}
