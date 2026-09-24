using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookSplice.FFmpeg.Probing;

internal sealed class FFprobeDocument
{
  [JsonPropertyName("streams")] public List<FFprobeStream>? Streams { get; set; }
  [JsonPropertyName("format")] public FFprobeFormat? Format { get; set; }
  [JsonPropertyName("chapters")] public List<FFprobeChapter>? Chapters { get; set; }
}
internal sealed class FFprobeStream
{
  public int Index { get; set; }
  [JsonPropertyName("codec_name")] public string? CodecName { get; set; }
  public string? Profile { get; set; }
  [JsonPropertyName("codec_type")] public string? CodecType { get; set; }
  public int? Channels { get; set; }
  [JsonPropertyName("channel_layout")] public string? ChannelLayout { get; set; }
  [JsonPropertyName("sample_rate")] public string? SampleRate { get; set; }
  public string? Duration { get; set; }
  [JsonPropertyName("start_time")] public string? StartTime { get; set; }
  [JsonPropertyName("time_base")] public string? TimeBase { get; set; }
  [JsonPropertyName("codec_tag_string")] public string? CodecTag { get; set; }
  [JsonPropertyName("extradata_hash")] public string? ExtradataHash { get; set; }
  [JsonPropertyName("bit_rate")] public string? BitRate { get; set; }
  [JsonPropertyName("nb_read_packets")] public string? NbReadPackets { get; set; }
  public int? Width { get; set; }
  public int? Height { get; set; }
  [JsonPropertyName("disposition")] public FFprobeDisposition? Disposition { get; set; }
  public Dictionary<string, JsonElement>? Tags { get; set; }
}
internal sealed class FFprobeDisposition { [JsonPropertyName("attached_pic")] public int AttachedPic { get; set; } }
internal sealed class FFprobeFormat
{
  public string? Duration { get; set; }
  [JsonPropertyName("format_name")] public string? FormatName { get; set; }
  public string? Size { get; set; }
  public Dictionary<string, JsonElement>? Tags { get; set; }
}
internal sealed class FFprobePacketDocument
{
  [JsonPropertyName("packets")] public List<FFprobePacket>? Packets { get; set; }
}
internal sealed class FFprobePacket
{
  public long? Duration { get; set; }
  [JsonPropertyName("side_data_list")] public List<FFprobePacketSideData>? SideData { get; set; }
}
internal sealed class FFprobePacketSideData
{
  [JsonPropertyName("side_data_type")] public string? Type { get; set; }
  [JsonPropertyName("skip_samples")] public long SkipSamples { get; set; }
  [JsonPropertyName("discard_padding")] public long DiscardPadding { get; set; }
}
internal sealed class FFprobeChapter
{
  public long Id { get; set; }
  [JsonPropertyName("start_time")] public string? StartTime { get; set; }
  [JsonPropertyName("end_time")] public string? EndTime { get; set; }
  [JsonPropertyName("time_base")] public string? TimeBase { get; set; }
  public Dictionary<string, JsonElement>? Tags { get; set; }
}
