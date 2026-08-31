namespace AudiobookConverter.FixtureGenerator;
#pragma warning disable CA1305

public sealed record FixtureDefinition(string CaseId, string RelativePath, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> ExpectedProperties, IReadOnlyList<string> Traits);

public static class FixtureCatalog
{
  public static IReadOnlyList<FixtureDefinition> Cases =>
  [
    Audio("case-mp3-mono-064", "mp3/mono-064.mp3", "sine=frequency=440:duration=1", "libmp3lame", "64k", 1, 44100, ["speech", "mono", "64kbps"]),
    Audio("case-mp3-mono-096", "mp3/mono-096.mp3", "sine=frequency=440:duration=1", "libmp3lame", "96k", 1, 44100, ["speech", "mono", "96kbps"]),
    Audio("case-mp3-stereo-128", "mp3/stereo-128.mp3", "sine=frequency=550:duration=1", "libmp3lame", "128k", 2, 44100, ["speech", "stereo", "128kbps"]),
    Audio("case-mp3-stereo-192", "mp3/stereo-192.mp3", "sine=frequency=660:duration=1", "libmp3lame", "192k", 2, 44100, ["speech", "stereo", "192kbps"]),
    Audio("case-aac-mono-44100", "aac/mono-44100.m4a", "sine=frequency=440:duration=1", "aac", "96k", 1, 44100, ["speech", "mono", "aac"]),
    Audio("case-aac-stereo-48000", "aac/stereo-48000.m4a", "sine=frequency=660:duration=1", "aac", "128k", 2, 48000, ["speech", "stereo", "aac"]),
    Audio("case-aac-mismatch-32000", "aac/mismatch-32000.m4a", "sine=frequency=550:duration=1", "aac", "96k", 1, 32000, ["speech", "mono", "mismatched-sample-rate"]),
    Audio("case-aac-mismatch-22050", "aac/mismatch-22050.m4a", "sine=frequency=770:duration=1", "aac", "96k", 1, 22050, ["speech", "mono", "mismatched-sample-rate"]),
    Audio("case-sine", "signals/sine.wav", "sine=frequency=440:duration=1", "pcm_s16le", null, 1, 44100, ["sine"]),
    Audio("case-pink-noise", "signals/pink.wav", "anoisesrc=color=pink:duration=1", "pcm_s16le", null, 1, 48000, ["pink-noise"]),
    new("case-silence", "signals/silence.wav", ["-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono", "-t", "1", "-c:a", "pcm_s16le"], Props("pcm_s16le", 1, 44100), ["silence"]),
    Audio("case-stereo", "signals/stereo.wav", "sine=frequency=440:duration=1", "pcm_s16le", null, 2, 44100, ["stereo"]),
    Audio("case-unicode-path", "unicode/テスト-日本語.m4a", "sine=frequency=880:duration=1", "aac", "96k", 1, 44100, ["unicode-path"]),
    new("case-jpeg-cover", "covers/jpeg.m4a", ["-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-f", "lavfi", "-i", "color=c=red:s=32x32:d=1", "-map", "0:a", "-map", "1:v", "-c:a", "aac", "-c:v", "mjpeg", "-disposition:v", "attached_pic"], new Dictionary<string, string>(Props("aac", 1, 44100)) { ["coverCodec"] = "mjpeg" }, ["jpeg-cover"]),
    new("case-png-cover", "covers/png.m4a", ["-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-f", "lavfi", "-i", "color=c=blue:s=32x32:d=1", "-map", "0:a", "-map", "1:v", "-c:a", "aac", "-c:v", "png", "-disposition:v", "attached_pic"], new Dictionary<string, string>(Props("aac", 1, 44100)) { ["coverCodec"] = "png" }, ["png-cover"]),
  ];

  private static FixtureDefinition Audio(string id, string path, string input, string codec, string? bitrate, int channels, int rate, string[] traits)
  {
    var arguments = new List<string> { "-f", "lavfi", "-i", input, "-ac", channels.ToString(), "-ar", rate.ToString(), "-c:a", codec };
    if (bitrate is not null) { arguments.Add("-b:a"); arguments.Add(bitrate); }
    return new FixtureDefinition(id, path, arguments, Props(codec == "libmp3lame" ? "mp3" : codec, channels, rate), traits);
  }

  private static Dictionary<string, string> Props(string codec, int channels, int rate) => new() { ["codec"] = codec, ["channels"] = channels.ToString(), ["sampleRate"] = rate.ToString() };
}
