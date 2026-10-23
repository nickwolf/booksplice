using BookSplice.Core.Settings;
using BookSplice.Gui.Updates;

namespace BookSplice.Gui.Tests;

public sealed class UpdateSelfCheckTests
{
  [Fact]
  public void SourceBuildCannotClaimPackagedIdentity()
  {
    Assert.False(PackagedIdentity.Matches("0.2.0"));
  }

  [Fact]
  public void SnapshotParserDoesNotMigrateAndDoesNotChangeBytes()
  {
    var old = "{\"schemaVersion\":0,\"outputPath\":\"C:\\\\output\"}"u8.ToArray();
    var original = old.ToArray();
    Assert.NotEqual(SettingsLoadCode.Valid, JsonSettingsStore.ParseSnapshot(old).Code);
    Assert.Equal(original, old);
    var future = "{\"schemaVersion\":2}"u8.ToArray();
    Assert.NotEqual(SettingsLoadCode.Valid, JsonSettingsStore.ParseSnapshot(future).Code);
    Assert.Equal(SettingsLoadCode.Missing, JsonSettingsStore.ParseSnapshot(null).Code);
  }

  [Fact]
  public void SnapshotParserAcceptsUnknownNullWithoutRewritingBytes()
  {
    var json = """
      {"schemaVersion":1,"outputDirectory":"C:\\output","qualityProfileId":"high-quality","channelPolicy":"PreserveSourceChannels","createChapters":true,"conversionJobs":null,"collisionPolicy":"AvoidCollision","metadataProfileId":"GenericMp4","validationLevel":"Lightweight","logLevel":"Information","futureOption":null}
      """;
    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
    var original = bytes.ToArray();
    Assert.Equal(SettingsLoadCode.Valid, JsonSettingsStore.ParseSnapshot(bytes).Code);
    Assert.Equal(original, bytes);
  }
}
