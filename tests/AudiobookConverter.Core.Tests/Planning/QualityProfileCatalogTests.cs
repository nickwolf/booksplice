using AudiobookConverter.Core.Planning;

namespace AudiobookConverter.Core.Tests.Planning;

public sealed class QualityProfileCatalogTests
{
  [Fact]
  public void Version1_orders_profiles_by_increasing_quality_and_bitrate()
  {
    var profiles = QualityProfileCatalog.Version1;

    Assert.Equal((IEnumerable<string>)["efficient", "balanced", "high-quality", "preserve-more"], profiles.Select(profile => profile.Id));
    Assert.Equal((IEnumerable<int>)[48, 80, 96, 128], profiles.Select(profile => profile.AudioBitrateKbps));
    Assert.True(profiles.Zip(profiles.Skip(1)).All(pair => pair.First.AudioBitrateKbps < pair.Second.AudioBitrateKbps));
  }

  [Fact]
  public void Version1_profiles_have_the_frozen_encoder_settings()
  {
    Assert.All(QualityProfileCatalog.Version1, profile =>
    {
      Assert.Equal(AacMode.NativeAacLc, profile.AacMode);
      Assert.Equal(ChannelPolicy.PreserveSourceChannels, profile.ChannelPolicy);
      Assert.Equal(SampleRatePolicy.PreserveCompatibleSource, profile.SampleRatePolicy);
      Assert.Equal(1, profile.Version);
      Assert.False(string.IsNullOrWhiteSpace(profile.DisplayName));
      Assert.False(string.IsNullOrWhiteSpace(profile.Description));
    });
  }

  [Fact]
  public void Version1_has_unique_stable_ids_and_no_custom_entry()
  {
    var profiles = QualityProfileCatalog.Version1;

    Assert.Equal(profiles.Count, profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count());
    Assert.DoesNotContain(profiles, profile => profile.Id.Equals("custom", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public void Version1_does_not_expose_a_local_recommendation_property()
  {
    Assert.DoesNotContain(typeof(QualityProfile).GetProperties(), property => property.Name == "IsLocalRecommended");
  }

  [Fact]
  public void Lookup_returns_profile_for_known_id_and_null_for_unknown_id()
  {
    Assert.Equal("high-quality", QualityProfileCatalog.FindById("high-quality")?.Id);
    Assert.Null(QualityProfileCatalog.FindById("unknown"));
    Assert.Null(QualityProfileCatalog.FindById(null));
  }

  [Fact]
  public void Version1_collection_cannot_be_modified_by_callers()
  {
    var profiles = QualityProfileCatalog.Version1;

    Assert.IsAssignableFrom<IReadOnlyList<QualityProfile>>(profiles);
    Assert.False(profiles is QualityProfile[]);
    Assert.Equal(4, profiles.Count);
  }
}



