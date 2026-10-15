using BookSplice.Updates.Handover;
using BookSplice.Updates.Policy;

namespace BookSplice.Updates.Tests.Policy;

public sealed class VersionPolicyTests
{
    [Fact]
    public void NumericPrecedenceIgnoresBuildMetadata()
    {
        Assert.True(SemanticVersion.ParseTag("v1.10.0").CompareTo(SemanticVersion.ParseTag("v1.9.0")) > 0);
        Assert.Equal(0, SemanticVersion.ParseTag("v1.2.3+a").CompareTo(SemanticVersion.ParseTag("v1.2.3+b")));
        Assert.True(SemanticVersion.ParseTag("v1.2.3-rc.1").IsPrerelease);
        Assert.Throws<FormatException>(() => SemanticVersion.ParseTag("v01.2.3"));
    }

    [Theory]
    [InlineData("v1.0.0-alpha", "v1.0.0-alpha.1")]
    [InlineData("v1.0.0-alpha.1", "v1.0.0-alpha.beta")]
    [InlineData("v1.0.0-alpha.beta", "v1.0.0-beta")]
    [InlineData("v1.0.0-beta", "v1.0.0-beta.2")]
    [InlineData("v1.0.0-beta.2", "v1.0.0-beta.11")]
    [InlineData("v1.0.0-beta.11", "v1.0.0-rc.1")]
    [InlineData("v1.0.0-rc.1", "v1.0.0")]
    [InlineData("v2.0.0", "v10.0.0")]
    [InlineData("v1.2.9", "v1.3.0")]
    public void PrecedenceFollowsSemVer(string earlier, string later)
    {
        Assert.True(SemanticVersion.ParseTag(earlier).CompareTo(SemanticVersion.ParseTag(later)) < 0);
        Assert.True(SemanticVersion.ParseTag(later).CompareTo(SemanticVersion.ParseTag(earlier)) > 0);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("V1.2.3")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3.4")]
    [InlineData("v1.02.3")]
    [InlineData("v1.2.03")]
    [InlineData("v1.2.3-01")]
    [InlineData("v1.2.3-a..b")]
    [InlineData("v1.2.3+")]
    [InlineData("v1.2.3+é")]
    [InlineData("v1.2.3-α")]
    [InlineData("v9223372036854775808.0.0")]
    [InlineData("v1.2.3-9223372036854775808")]
    public void InvalidOrOversizedTagIsRejected(string tag)
    {
        Assert.Throws<FormatException>(() => SemanticVersion.ParseTag(tag));
    }

    [Fact]
    public void ParsingAndComparisonIgnoreCurrentCulture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ar-EG");
            Assert.Equal(0, SemanticVersion.ParseTag("v1.2.3+build.01").CompareTo(SemanticVersion.ParseTag("v1.2.3+other")));
            Assert.True(SemanticVersion.ParseTag("v1.2.3-9").CompareTo(SemanticVersion.ParseTag("v1.2.3-10")) < 0);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ResourceLimitsAreExactAndUseLongArithmetic()
    {
        Assert.Equal(100, UpdateLimits.ReleasePageSize);
        Assert.Equal(10, UpdateLimits.MaxReleasePages);
        Assert.Equal(TimeSpan.FromSeconds(10), UpdateLimits.ApiRequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), UpdateLimits.DownloadIdleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(30), UpdateLimits.StagingTimeout);
        Assert.Equal(2L * 1024 * 1024, UpdateLimits.ReleasePageMaxBytes);
        Assert.Equal(256L * 1024, UpdateLimits.NotesMaxBytes);
        Assert.Equal(1L * 1024 * 1024, UpdateLimits.ManifestMaxBytes);
        Assert.Equal(16L * 1024, UpdateLimits.SignatureEnvelopeMaxBytes);
        Assert.Equal(1L * 1024 * 1024 * 1024, UpdateLimits.ZipMaxBytes);
        Assert.Equal(20_000, UpdateLimits.MaxArchiveEntries);
        Assert.Equal(1L * 1024 * 1024 * 1024, UpdateLimits.MaxExtractedFileBytes);
        Assert.Equal(4L * 1024 * 1024 * 1024, UpdateLimits.MaxExtractedTotalBytes);
        Assert.Equal(TimeSpan.FromSeconds(60), UpdateLimits.SelfCheckTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), UpdateLimits.ReadinessTimeout);
        Assert.Equal(1, UpdateProtocol.ProtocolVersion);
    }
}
