namespace BookSplice.Core.Planning;

public enum AacMode
{
  NativeAacLc
}

public enum ChannelPolicy
{
  PreserveSourceChannels,
  ForceMono,
  ForceStereo
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
