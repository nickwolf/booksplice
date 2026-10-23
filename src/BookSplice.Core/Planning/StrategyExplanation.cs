namespace BookSplice.Core.Planning;

public sealed record StrategyExplanation(string Summary, IReadOnlyList<string> Reasons)
{
  private const string Prefix = "stream-copy.";
  private const string SegmentLimitCode = "strategy.filter-concat-file-limit";

  public static StrategyExplanation Create(AudioStrategy strategy, IEnumerable<string> reasonCodes)
  {
    ArgumentNullException.ThrowIfNull(reasonCodes);
    if (strategy == AudioStrategy.AacStreamCopy) return new("Audio is copied without re-encoding.", []);
    var codes = reasonCodes.ToHashSet(StringComparer.Ordinal);
    var summary = strategy == AudioStrategy.SegmentedTranscode || codes.Contains(SegmentLimitCode)
      ? $"Audio will be re-encoded to AAC. With more than {ConversionPlanner.FilterConcatMaximumFiles} files it is encoded in separate segments that are joined afterward."
      : "Audio will be re-encoded to AAC.";
    var reasons = new List<string>();
    var known = new HashSet<string>(StringComparer.Ordinal) { SegmentLimitCode };
    void Add(string message, params string[] matching)
    {
      var hit = false;
      foreach (var code in matching) if (codes.Contains(Prefix + code)) { hit = true; known.Add(Prefix + code); }
      if (hit) reasons.Add(message);
    }
    Add("There are no source files.", "no-sources");
    Add("The source audio is not AAC.", "codec.unsupported");
    Add("The source AAC is not the LC profile.", "profile.unsupported");
    Add("The source has more than two channels or an unsupported channel count.", "channel-count.unsupported");
    Add("A source file does not have exactly one audio stream.", "audio-stream-count");
    Add("A source file is not in an MP4-family container.", "container.unsupported");
    Add("A source file is already an .m4b file.", "existing-m4b");
    Add("The source audio does not start at time zero.", "timestamp.negative", "timestamp.nonzero");
    Add("The selected channel setting does not keep the source channel layout.", "channel-policy");
    Add("The source files have different sample rates.", "sample-rate.mismatch");
    Add("The source files have different channel counts.", "channel-count.mismatch");
    Add("The source files have different channel layouts.", "channel-layout.mismatch");
    Add("The source files were encoded with different AAC settings.", "time-base.mismatch", "codec-tag.mismatch", "extradata-hash.mismatch");
    var missing = MissingEvidence.Where(item => codes.Contains($"{Prefix}{item.Code}.missing") || (item.Code == "start-time" && codes.Contains(Prefix + "timestamp.missing"))).ToArray();
    foreach (var code in codes.Where(code => code.EndsWith(".missing", StringComparison.Ordinal))) known.Add(code);
    if (missing.Length > 0) reasons.Add($"Could not confirm that the source audio can be copied because the probe did not report its {JoinNames(missing.Select(item => item.Name).ToArray())}.");
    if (codes.Any(code => !known.Contains(code))) reasons.Add("The source audio did not pass another stream copy check.");
    return new(summary, reasons);
  }

  private static readonly (string Code, string Name)[] MissingEvidence =
  [
    ("profile", "profile"), ("channel-count", "channel count"), ("channel-layout", "channel layout"), ("sample-rate", "sample rate"),
    ("time-base", "time base"), ("codec-tag", "codec tag"), ("extradata-hash", "codec setup data"), ("start-time", "start time"),
  ];

  private static string JoinNames(string[] names) => names.Length switch
  {
    1 => names[0],
    2 => $"{names[0]} and {names[1]}",
    _ => $"{string.Join(", ", names[..^1])}, and {names[^1]}",
  };
}
