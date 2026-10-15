using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BookSplice.Updates.Policy;

namespace BookSplice.Updates.Transport;

public interface IReleaseClient
{
  Task<IReadOnlyList<ReleaseCandidate>> FetchAsync(CancellationToken cancellationToken);
}

public sealed class GitHubReleaseClient : IReleaseClient, IDisposable
{
  private readonly ValidatedHttpTransport _transport;

  public GitHubReleaseClient(HttpMessageHandler handler, TimeProvider clock)
  {
    _transport = new ValidatedHttpTransport(handler, clock);
  }

  public GitHubReleaseClient(TimeProvider clock)
  {
    _transport = ValidatedHttpTransport.CreateProduction(clock);
  }

  public async Task<IReadOnlyList<ReleaseCandidate>> FetchAsync(CancellationToken cancellationToken)
  {
    var releases = new List<ReleaseCandidate>();
    var page = 1;
    while (true)
    {
      var expected = PageUri(page);
      var (body, link) = await _transport.GetReleasePageAsync(expected, cancellationToken);
      try
      {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
          throw new ReleaseTransportException("Release API page is not an array.");
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
          var tag = item.GetProperty("tag_name").GetString() ?? "";
          SemanticVersion version;
          try
          {
            version = SemanticVersion.ParseTag(tag);
          }
          catch (FormatException)
          {
            continue;
          }

          var id = item.GetProperty("id").GetInt64();
          var pageUri = new Uri(item.GetProperty("html_url").GetString()!);
          if (id <= 0 || !ValidReleasePage(pageUri, tag))
          {
            throw new ReleaseTransportException("Release identity is invalid.");
          }

          var assets = new List<ReleaseAsset>();
          var assetNames = new HashSet<string>(StringComparer.Ordinal);
          foreach (var asset in item.GetProperty("assets").EnumerateArray())
          {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!assetNames.Add(name))
            {
              throw new ReleaseTransportException("Release has duplicate asset names.");
            }

            assets.Add(new ReleaseAsset(asset.GetProperty("id").GetInt64(), name,
              new Uri(asset.GetProperty("browser_download_url").GetString()!), asset.GetProperty("size").GetInt64()));
          }

          releases.Add(new ReleaseCandidate(id, tag, version, pageUri,
            PlainNotes(item.TryGetProperty("body", out var notes) ? notes.GetString() : null),
            item.GetProperty("draft").GetBoolean(), item.GetProperty("prerelease").GetBoolean(), assets));
        }
      }
      catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or UriFormatException or OverflowException or FormatException or ArgumentException)
      {
        throw new ReleaseTransportException($"Release API page contains invalid metadata: {exception.Message}");
      }

      var next = NextLink(link);
      if (next is null)
      {
        return releases;
      }

      if (page == UpdateLimits.MaxReleasePages)
      {
        throw new ReleaseTransportException("Release pagination exceeds ten pages.");
      }

      page++;
      if (!ValidatedHttpTransport.SameApiPage(next, PageUri(page)))
      {
        throw new ReleaseTransportException("Release pagination does not point to the next fixed repository page.");
      }
    }
  }

  private static Uri PageUri(int page) => new($"https://api.github.com/repos/nickwolf/booksplice/releases?per_page=100&page={page}");

  private static Uri? NextLink(string? header)
  {
    if (string.IsNullOrWhiteSpace(header))
    {
      return null;
    }

    Uri? next = null;
    foreach (var part in header.Split(','))
    {
      var match = Regex.Match(part.Trim(), "^<([^>]+)>;\\s*rel=\"?([^\";]+)\"?$", RegexOptions.CultureInvariant);
      if (!match.Success)
      {
        throw new ReleaseTransportException("Release pagination header is malformed.");
      }

      if (match.Groups[2].Value != "next")
      {
        continue;
      }

      if (next is not null || !Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out next) || !ValidatedHttpTransport.ValidApiUri(next))
      {
        throw new ReleaseTransportException("Release pagination URL is invalid.");
      }
    }

    return next;
  }

  private static bool ValidReleasePage(Uri uri, string tag) =>
    uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com" && uri.IsDefaultPort &&
    uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
    uri.AbsolutePath == $"/nickwolf/booksplice/releases/tag/{Uri.EscapeDataString(tag)}";

  private static string PlainNotes(string? notes)
  {
    if (string.IsNullOrEmpty(notes))
    {
      return "";
    }

    var text = Regex.Replace(notes, "<[^>]*>", "", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    text = System.Net.WebUtility.HtmlDecode(text);
    var builder = new StringBuilder();
    var bytes = 0;
    foreach (var rune in text.EnumerateRunes())
    {
      if (rune.Value is < 32 and not (9 or 10 or 13) || rune.Value == 127)
      {
        continue;
      }

      var size = rune.Utf8SequenceLength;
      if (bytes + size > UpdateLimits.NotesMaxBytes)
      {
        break;
      }

      builder.Append(rune.ToString());
      bytes += size;
    }

    return builder.ToString();
  }

  public void Dispose() => _transport.Dispose();
}
