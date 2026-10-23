using System.Net;
using System.Text.Json;

namespace BookSplice.Updates.Transport;

// Test builds only: serves GitHub-shaped release metadata and assets from a local directory
// (one subdirectory per tag, for example v0.9.1) through the normal transport, so URL, size,
// checksum and signature checks all run unchanged. Release builds always get null.
public static class TestReleaseSource
{
  public const string DirectoryVariable = "BOOKSPLICE_TEST_RELEASE_DIR";

  public static HttpMessageHandler? CreateHandler() => Create(UpdateBuild.Flavor, Environment.GetEnvironmentVariable);

  internal static HttpMessageHandler? Create(BuildFlavor flavor, Func<string, string?> environment) =>
    UpdateBuild.TestVariable(flavor, DirectoryVariable, environment) is { Length: > 0 } directory ?
      new LocalHandler(Path.GetFullPath(directory)) : null;

  private sealed class LocalHandler(string root) : HttpMessageHandler
  {
    private const string Repository = "https://github.com/nickwolf/booksplice/releases/";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
      CancellationToken cancellationToken)
    {
      var uri = request.RequestUri!;
      if (uri.Host == "api.github.com") return Task.FromResult(Releases(uri));
      var segments = uri.AbsolutePath.Split('/');
      if (uri.Host == "github.com" && segments.Length == 7 && segments[4] == "download")
      {
        var tag = Uri.UnescapeDataString(segments[5]);
        var name = Uri.UnescapeDataString(segments[6]);
        var file = Directory.Exists(root) ? Directory.EnumerateDirectories(root)
          .Where(directory => Path.GetFileName(directory) == tag)
          .SelectMany(Directory.EnumerateFiles).FirstOrDefault(path => Path.GetFileName(path) == name) : null;
        if (file is not null)
        {
          var content = new StreamContent(File.OpenRead(file));
          content.Headers.ContentLength = new FileInfo(file).Length;
          return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
      }
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private HttpResponseMessage Releases(Uri uri)
    {
      using var stream = new MemoryStream();
      using (var json = new Utf8JsonWriter(stream))
      {
        json.WriteStartArray();
        var directories = Directory.Exists(root) && uri.Query.Contains("page=1", StringComparison.Ordinal) ?
          Directory.EnumerateDirectories(root).OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray() : [];
        long assetId = 1000;
        for (var i = 0; i < directories.Length; i++)
        {
          var tag = Path.GetFileName(directories[i]);
          json.WriteStartObject();
          json.WriteString("tag_name", tag);
          json.WriteNumber("id", i + 1);
          json.WriteString("html_url", Repository + "tag/" + Uri.EscapeDataString(tag));
          json.WriteString("body", "Local rehearsal release.");
          json.WriteBoolean("draft", false);
          json.WriteBoolean("prerelease", false);
          json.WriteStartArray("assets");
          foreach (var file in Directory.EnumerateFiles(directories[i]).OrderBy(Path.GetFileName, StringComparer.Ordinal))
          {
            json.WriteStartObject();
            json.WriteNumber("id", ++assetId);
            json.WriteString("name", Path.GetFileName(file));
            json.WriteString("browser_download_url", Repository + "download/" + Uri.EscapeDataString(tag) +
              "/" + Uri.EscapeDataString(Path.GetFileName(file)));
            json.WriteNumber("size", new FileInfo(file).Length);
            json.WriteEndObject();
          }
          json.WriteEndArray();
          json.WriteEndObject();
        }
        json.WriteEndArray();
      }
      return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) };
    }
  }
}
