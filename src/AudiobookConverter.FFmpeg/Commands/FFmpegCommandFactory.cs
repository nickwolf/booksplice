using AudiobookConverter.Core.Covers;
using AudiobookConverter.Core.Planning;
using AudiobookConverter.FFmpeg.Execution;
using AudiobookConverter.FFmpeg.Tools;
namespace AudiobookConverter.FFmpeg.Commands;

public sealed class FFmpegCommandFactory(MediaToolSet tools)
{
  public ProcessSpec CreateFinal(ConversionPlan plan, string temporaryOutput, string? concatManifest = null, string? filterGraph = null, string? metadataFile = null)
  {
    ArgumentNullException.ThrowIfNull(plan);
    var args = BaseArguments();
    var useConcat = plan.Strategy == AudioStrategy.SegmentedTranscode || plan.Strategy == AudioStrategy.AacStreamCopy && plan.SourcePaths.Count > 1;
    var sourceInputs = useConcat ? 1 : plan.SourcePaths.Count;
    if (useConcat)
    {
      args.AddRange(["-f", "concat", "-safe", "0", "-i", concatManifest ?? throw new ArgumentException("A concat manifest is required.", nameof(concatManifest))]);
    }
    else
    {
      foreach (var source in plan.SourcePaths) args.AddRange(["-i", source]);
    }
    var metadataIndex = -1;
    if (metadataFile is not null)
    {
      metadataIndex = sourceInputs;
      args.AddRange(["-f", "ffmetadata", "-i", metadataFile]);
    }
    var coverIndex = -1;
    if (plan.Cover is { } cover)
    {
      coverIndex = sourceInputs + (metadataIndex >= 0 ? 1 : 0);
      args.AddRange(["-i", cover.SourcePath]);
    }
    if (plan.Strategy == AudioStrategy.FilterConcatTranscode)
    {
      args.AddRange(["-/filter_complex", filterGraph ?? throw new ArgumentException("A filter graph path is required.", nameof(filterGraph)), "-map", "[aout]"]);
      AddEncode(args, plan);
    }
    else
    {
      args.AddRange(["-map", "0:a:0"]);
      if (plan.Strategy is AudioStrategy.AacStreamCopy or AudioStrategy.SegmentedTranscode) args.AddRange(["-c:a", "copy"]);
      else AddEncode(args, plan);
    }
    AddCoverMap(args, plan.Cover, coverIndex);
    if (metadataIndex >= 0)
    {
      var index = metadataIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
      args.AddRange(["-map_metadata", index, "-map_chapters", index]);
    }
    args.AddRange(["-movflags", "+faststart", "-f", "ipod", "-y", temporaryOutput]);
    return new ProcessSpec(tools.FFmpegPath, args);
  }
  public ProcessSpec CreateSegment(ConversionPlan plan, string source, string output)
  {
    var args = BaseArguments();
    args.AddRange(["-i", source, "-map", "0:a:0"]);
    AddEncode(args, plan);
    args.AddRange(["-f", "adts", "-y", output]);
    return new ProcessSpec(tools.FFmpegPath, args);
  }
  private static List<string> BaseArguments() =>
  [
    "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1", "-nostats"
  ];

  private static void AddEncode(List<string> args, ConversionPlan plan)
  {
    var bitrate = $"{plan.QualityProfile.AudioBitrateKbps}k";
    var sampleRate = plan.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
    var channels = plan.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture);
    args.AddRange(["-c:a", "aac", "-profile:a", "aac_low", "-b:a", bitrate, "-ar", sampleRate, "-ac", channels]);
  }

  private static void AddCoverMap(List<string> args, CoverCandidate? cover, int coverInput)
  {
    if (cover is null) return;
    var map = cover.Origin == CoverOrigin.EmbeddedPicture
      ? $"{coverInput}:{cover.EmbeddedPictureIndex ?? 0}"
      : $"{coverInput}:v:0";
    args.AddRange(["-map", map, "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
  }
}
