namespace AudiobookConverter.Core.Planning;

public static class QualityProfileCatalog
{
  private static readonly IReadOnlyList<QualityProfile> Version1Profiles = Array.AsReadOnly(new QualityProfile[]
  {
    new("efficient", "Efficient", "Compact AAC-LC output for spoken-word sources.", AacMode.NativeAacLc, 48, ChannelPolicy.PreserveSourceChannels, SampleRatePolicy.PreserveCompatibleSource, 1),
    new("balanced", "Balanced", "A practical balance of size and listening quality.", AacMode.NativeAacLc, 80, ChannelPolicy.PreserveSourceChannels, SampleRatePolicy.PreserveCompatibleSource, 1),
    new("high-quality", "High quality", "Higher-quality AAC-LC output for demanding listening.", AacMode.NativeAacLc, 96, ChannelPolicy.PreserveSourceChannels, SampleRatePolicy.PreserveCompatibleSource, 1),
    new("preserve-more", "Preserve more", "The highest V1 bitrate for retaining more source detail.", AacMode.NativeAacLc, 128, ChannelPolicy.PreserveSourceChannels, SampleRatePolicy.PreserveCompatibleSource, 1)
  });

  public static IReadOnlyList<QualityProfile> Version1 => Version1Profiles;

  public static QualityProfile? FindById(string? id) => id is null
    ? null
    : Version1Profiles.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.Ordinal));
}




