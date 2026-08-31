namespace AudiobookConverter.Core.Planning;

public enum AacMode
{
  NativeAacLc
}

public enum ChannelPolicy
{
  PreserveSourceChannels
}

public enum SampleRatePolicy
{
  PreserveCompatibleSource
}

public sealed record QualityProfile(
  string Id,
  string DisplayName,
  string Description,
  AacMode AacMode,
  int AudioBitrateKbps,
  ChannelPolicy ChannelPolicy,
  SampleRatePolicy SampleRatePolicy,
  int Version);
