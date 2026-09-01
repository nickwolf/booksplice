using AudiobookConverter.Core.Covers;
using AudiobookConverter.Core.Planning;
using AudiobookConverter.FFmpeg.Execution;
using AudiobookConverter.FFmpeg.Tools;

namespace AudiobookConverter.FFmpeg.Commands;

public sealed class FFmpegCommandFactory(MediaToolSet tools)
{
  public ProcessSpec CreateFinal(ConversionPlan plan, string temporaryOutput, string? concatManifest = null, string? filterGraph = null, IReadOnlyList<string>? segments = null, string? metadataFile = null)
  {
    var args = BaseArguments();
    if (plan.Strategy == AudioStrategy.AacStreamCopy && concatManifest is not null) { args.AddRange(["-f", "concat", "-safe", "0", "-i", concatManifest]); }
    else if (plan.Strategy == AudioStrategy.SegmentedTranscode && segments is not null) { args.AddRange(["-f", "concat", "-safe", "0", "-i", concatManifest!]); }
    else foreach (var source in plan.SourcePaths) args.AddRange(["-i", source]);
    if (metadataFile is not null) args.AddRange(["-i", metadataFile]);
    AddCover(args, plan.Cover);
    if (plan.Strategy == AudioStrategy.FilterConcatTranscode) { args.AddRange(["-filter_complex_script", filterGraph!, "-map", "[aout]"]); AddEncode(args, plan); }
    else { args.AddRange(["-map", "0:a:0"]); if (plan.Strategy == AudioStrategy.AacStreamCopy || plan.Strategy == AudioStrategy.SegmentedTranscode) args.AddRange(["-c:a", "copy"]); else AddEncode(args, plan); }
    if (plan.Cover is not null) args.AddRange(["-map", "1:v:0", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
    if (metadataFile is not null) args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1"]);
    args.AddRange(["-movflags", "+faststart", "-f", "ipod", "-y", temporaryOutput]);
    return new ProcessSpec(tools.FFmpegPath, args);
  }
  public ProcessSpec CreateSegment(ConversionPlan plan, string source, string output) { var args = BaseArguments(); args.AddRange(["-i", source, "-map", "0:a:0"]); AddEncode(args, plan); args.AddRange(["-f", "adts", "-y", output]); return new ProcessSpec(tools.FFmpegPath, args); }
  private static List<string> BaseArguments() => ["-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1", "-nostats"];
  private static void AddEncode(List<string> args, ConversionPlan plan) { args.AddRange(["-c:a", "aac", "-profile:a", "aac_low", "-b:a", $"{plan.QualityProfile.AudioBitrateKbps}k"]); }
  private static void AddCover(List<string> args, CoverCandidate? cover) { if (cover is not null) args.AddRange(["-i", cover.SourcePath]); }
}
