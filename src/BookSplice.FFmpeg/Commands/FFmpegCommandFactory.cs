using BookSplice.Core.Covers;
using BookSplice.Core.Planning;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tools;
namespace BookSplice.FFmpeg.Commands;

public sealed class FFmpegCommandFactory(MediaToolSet tools)
{
  public ProcessSpec CreateFinal(ConversionPlan plan, string temporaryOutput, string? concatManifest = null, string? filterGraph = null, string? metadataFile = null, string? coverFile = null, string? intermediateAudio = null)
  {
    ArgumentNullException.ThrowIfNull(plan);
    var args = BaseArguments();
    var useConcat = plan.Strategy == AudioStrategy.AacStreamCopy && plan.SourcePaths.Count > 1;
    var segmented = plan.Strategy == AudioStrategy.SegmentedTranscode;
    var sourceInputs = useConcat || segmented ? 1 : plan.SourcePaths.Count;
    if (segmented)
    {
      args.AddRange(["-i", intermediateAudio ?? throw new ArgumentException("The joined audio file is required.", nameof(intermediateAudio))]);
    }
    else if (useConcat)
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
      args.AddRange(["-i", cover.Origin == CoverOrigin.EmbeddedPicture ? coverFile ?? throw new ArgumentException("An extracted cover file is required for an embedded cover.", nameof(coverFile)) : cover.SourcePath]);
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
  public ProcessSpec CreateCoverExtraction(CoverCandidate cover, string output)
  {
    ArgumentNullException.ThrowIfNull(cover);
    if (cover.EmbeddedPictureIndex is not >= 0) throw new ArgumentException("The embedded cover has no stream index.", nameof(cover));
    var index = cover.EmbeddedPictureIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    return new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", cover.SourcePath, "-map", $"0:{index}", "-c:v", "copy", "-frames:v", "1", "-f", "image2pipe", "-y", output]);
  }
  // Decodes one source through the same chain the encoder sees. The verbose summary on stderr gives the sample count.
  public ProcessSpec CreateMeasure(ConversionPlan plan, string source)
    => new(tools.FFmpegPath, ["-hide_banner", "-loglevel", "verbose", "-nostdin", "-i", source, "-map", "0:a:0", "-vn", "-af", FilterGraphWriter.SourceChain(plan.SampleRate, plan.Channels), "-c:a", "pcm_s16le", "-f", "null", "-"]);
  public ProcessSpec CreateRange(ConversionPlan plan, IReadOnlyList<string> sources, long trimStart, long trimEnd, string output)
  {
    var args = BaseArguments();
    foreach (var source in sources) args.AddRange(["-i", source]);
    args.AddRange(["-filter_complex", FilterGraphWriter.WriteRange(sources.Count, plan.SampleRate, plan.Channels, trimStart, trimEnd), "-map", "[aout]"]);
    AddEncode(args, plan);
    args.AddRange(["-f", "adts", "-y", output]);
    return new ProcessSpec(tools.FFmpegPath, args);
  }
  // The offset puts the encoder's priming frame before time zero. Muxing ADTS straight into the final file would give the chapter track the ADTS time base.
  public ProcessSpec CreateIntermediate(ConversionPlan plan, string joined, string output)
  {
    var offset = (-FrameSamples / (double)plan.SampleRate).ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
    return new ProcessSpec(tools.FFmpegPath, [.. BaseArguments(), "-itsoffset", offset, "-i", joined, "-c", "copy", "-f", "ipod", "-y", output]);
  }
  private const int FrameSamples = SegmentRangePlanner.FrameSamples;
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
    args.AddRange(["-map", $"{coverInput}:v:0", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
  }
}
