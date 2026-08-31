namespace AudiobookConverter.FixtureGenerator;

public sealed record FixtureDefinition(string CaseId, string RelativePath, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> ExpectedProperties, IReadOnlyList<string> Traits);

public static class FixtureCatalog
{
  public static IReadOnlyList<FixtureDefinition> Cases =>
  [
    new("case-mp3-mono-064", "mp3/mono-064.mp3", ["-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-ac", "1", "-ar", "44100", "-c:a", "libmp3lame", "-b:a", "64k"], new Dictionary<string,string>{{"codec","mp3"},{"channels","1"},{"bitrate","64000"}}, ["speech","mono","64kbps"]),
    new("case-mp3-mono-096", "mp3/mono-096.mp3", ["-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-ac", "1", "-c:a", "libmp3lame", "-b:a", "96k"], new Dictionary<string,string>{{"codec","mp3"},{"channels","1"},{"bitrate","96000"}}, ["speech","mono","96kbps"]),
    new("case-mp3-stereo-128", "mp3/stereo-128.mp3", ["-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-ac", "2", "-c:a", "libmp3lame", "-b:a", "128k"], new Dictionary<string,string>{{"codec","mp3"},{"channels","2"},{"bitrate","128000"}}, ["stereo","128kbps"]),
    new("case-mp3-stereo-192", "mp3/stereo-192.mp3", ["-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-ac", "2", "-ar", "44100", "-c:a", "libmp3lame", "-b:a", "192k"], new Dictionary<string,string>{{"codec","mp3"},{"channels","2"},{"bitrate","192000"}}, ["stereo","192kbps"]),
    new("case-aac-mono-44100", "aac/mono-44100.m4a", ["-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-ac", "1", "-ar", "44100", "-c:a", "aac", "-b:a", "96k"], new Dictionary<string,string>{{"codec","aac"},{"channels","1"},{"sample_rate","44100"}}, ["mono"]),
    new("case-aac-stereo-48000", "aac/stereo-48000.m4a", ["-f", "lavfi", "-i", "sine=frequency=660:duration=1", "-ac", "2", "-ar", "48000", "-c:a", "aac", "-b:a", "128k"], new Dictionary<string,string>{{"codec","aac"},{"channels","2"},{"sample_rate","48000"}}, ["stereo"]),
    new("case-aac-mismatch-32000", "aac/mismatch-32000.m4a", ["-f", "lavfi", "-i", "sine=frequency=550:duration=1", "-ac", "1", "-ar", "32000", "-c:a", "aac", "-b:a", "96k"], new Dictionary<string,string>{{"codec","aac"},{"sample_rate","32000"}}, ["mismatched-sample-rate"]),
    new("case-silence", "silence/silence.wav", ["-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono", "-t", "1", "-c:a", "pcm_s16le"], new Dictionary<string,string>{{"codec","pcm_s16le"}}, ["silence"]),
    new("case-pink-noise", "noise/pink.wav", ["-f", "lavfi", "-i", "anoisesrc=color=pink:duration=1", "-c:a", "pcm_s16le"], new Dictionary<string,string>{{"codec","pcm_s16le"}}, ["pink-noise"])
  ];
}
