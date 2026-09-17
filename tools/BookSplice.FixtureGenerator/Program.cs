using System.Text.Json;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.FixtureGenerator;
#pragma warning disable CA1305, CA1826, CA1859

public static class Program
{
  private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

  public static async Task<int> Main(string[] args)
  {
    var longFixture = args.Contains("--long", StringComparer.Ordinal);
    var values = args.Where(arg => !string.Equals(arg, "--long", StringComparison.Ordinal)).ToArray();
    var root = values.Length > 0 ? Path.GetFullPath(values[0]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "fixtures");
    var toolsDir = values.Length > 1 ? values[1] : Path.Combine(Environment.CurrentDirectory, "artifacts", "tools", "ffmpeg");
    var tools = new MediaToolLocator(toolsDir).Resolve();
    var runner = new ProcessRunner(); var probe = new FFprobeMediaProbe(runner, tools);
    Directory.CreateDirectory(root);
    foreach (var item in FixtureCatalog.Cases) await GenerateAsync(runner, tools.FFmpegPath, root, item).ConfigureAwait(false);
    await GenerateChapteredAsync(runner, tools.FFmpegPath, root).ConfigureAwait(false);
    var trackOrder = await GenerateTracksAsync(runner, tools.FFmpegPath, root).ConfigureAwait(false);
    await GenerateCorruptAsync(root).ConfigureAwait(false);
    if (longFixture) await GenerateLongAsync(runner, tools.FFmpegPath, root).ConfigureAwait(false);

    var cases = new List<object>();
    foreach (var item in FixtureCatalog.Cases)
    {
      var path = Path.Combine(root, item.RelativePath); await VerifyAsync(probe, path, item.ExpectedProperties).ConfigureAwait(false);
      cases.Add(Case(item.CaseId, path, item.Traits, [Path.GetFileName(path)], item.Arguments, item.ExpectedProperties));
    }
    var chaptered = Path.Combine(root, "chapters", "chaptered.m4a");
    var chapterProps = new Dictionary<string, string> { ["codec"] = "aac", ["chapters"] = "2" };
    await VerifyAsync(probe, chaptered, chapterProps).ConfigureAwait(false);
    cases.Add(Case("case-chaptered", chaptered, ["chapters"], ["chaptered.m4a"], ["ffmetadata", "two deterministic chapters"], chapterProps));
    var tracks = Path.Combine(root, "many-tracks");
    foreach (var name in trackOrder) await VerifyAsync(probe, Path.Combine(tracks, name), new Dictionary<string, string> { ["codec"] = "aac", ["channels"] = "1", ["sampleRate"] = "44100" }).ConfigureAwait(false);
    cases.Add(Case("case-many-short-tracks", tracks, ["many-short-tracks"], trackOrder, ["eight ordered AAC tracks"], new Dictionary<string, string> { ["trackCount"] = trackOrder.Count.ToString() }));
    cases.Add(Case("case-corrupt-truncated", Path.Combine(root, "corrupt", "truncated.mp3"), ["corrupt"], ["truncated.mp3"], ["one byte truncation"], new Dictionary<string, string>(), true));
    await File.WriteAllTextAsync(Path.Combine(root, "corpus.json"), JsonSerializer.Serialize(new { schemaVersion = "1", generatedBy = "BookSplice.FixtureGenerator", cases }, JsonOptions)).ConfigureAwait(false);
    return 0;
  }

  private static object Case(string id, string path, IReadOnlyList<string> traits, IReadOnlyList<string> order, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> properties, bool corrupt = false) => new { caseId = id, sourcePath = path, traits, expectedOrder = order, groups = new[] { "smoke" }, copyPermission = true, arguments, expectedProperties = properties, expectedCorrupt = corrupt };
  private static async Task GenerateAsync(IProcessRunner runner, string ffmpeg, string root, FixtureDefinition item) { var path = Path.Combine(root, item.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await RunAsync(runner, ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", .. item.Arguments, path]).ConfigureAwait(false); }
  private static async Task GenerateChapteredAsync(IProcessRunner runner, string ffmpeg, string root) { var dir = Path.Combine(root, "chapters"); Directory.CreateDirectory(dir); var metadata = Path.Combine(dir, "chapters.ffmeta"); await File.WriteAllTextAsync(metadata, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1000\ntitle=One\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=1000\nEND=2000\ntitle=Two\n").ConfigureAwait(false); await RunAsync(runner, ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-i", metadata, "-map_metadata", "1", "-c:a", "aac", Path.Combine(dir, "chaptered.m4a")]).ConfigureAwait(false); }
  private static async Task<IReadOnlyList<string>> GenerateTracksAsync(IProcessRunner runner, string ffmpeg, string root) { var dir = Path.Combine(root, "many-tracks"); Directory.CreateDirectory(dir); var names = Enumerable.Range(1, 8).Select(number => $"{number:000}.m4a").ToArray(); for (var i = 0; i < names.Length; i++) await RunAsync(runner, ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency={300 + i * 20}:duration=0.25", "-ac", "1", "-ar", "44100", "-c:a", "aac", Path.Combine(dir, names[i])]).ConfigureAwait(false); return names; }
  private static async Task GenerateCorruptAsync(string root) { var target = Path.Combine(root, "corrupt", "truncated.mp3"); Directory.CreateDirectory(Path.GetDirectoryName(target)!); await File.WriteAllBytesAsync(target, [0x49]).ConfigureAwait(false); }
  private static Task GenerateLongAsync(IProcessRunner runner, string ffmpeg, string root) { var target = Path.Combine(root, "long", "long-60s.wav"); Directory.CreateDirectory(Path.GetDirectoryName(target)!); return RunAsync(runner, ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=60", "-c:a", "pcm_s16le", target]); }
  private static async Task VerifyAsync(FFprobeMediaProbe probe, string path, IReadOnlyDictionary<string, string> expected) { var media = await probe.ProbeAsync(path).ConfigureAwait(false); var audio = media.AudioStreams.Count > 0 ? media.AudioStreams[0] : null; var cover = media.AttachedPictures.Count > 0 ? media.AttachedPictures[0] : null; if (audio is null || !Matches(audio.CodecName, expected, "codec") || !Matches(audio.Channels?.ToString(System.Globalization.CultureInfo.InvariantCulture), expected, "channels") || !Matches(audio.SampleRate?.ToString(System.Globalization.CultureInfo.InvariantCulture), expected, "sampleRate") || !Matches(media.Chapters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), expected, "chapters") || !Matches(cover?.CodecName, expected, "coverCodec")) throw new InvalidOperationException($"Generated fixture did not meet structural properties: {Path.GetFileName(path)}."); }
  private static bool Matches(string? actual, IReadOnlyDictionary<string, string> expected, string key) => !expected.TryGetValue(key, out var required) || string.Equals(actual, required, StringComparison.OrdinalIgnoreCase);
  private static async Task RunAsync(IProcessRunner runner, string ffmpeg, IReadOnlyList<string> args) { var result = await runner.RunAsync(new ProcessSpec(ffmpeg, args), null, CancellationToken.None).ConfigureAwait(false); if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError); }
}
