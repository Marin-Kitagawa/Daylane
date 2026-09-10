using Daylane.Services;

namespace Daylane.Tests;

public class VersionCompareTests
{
    [Theory]
    [InlineData("v1.0.2", "1.0.1")]
    [InlineData("1.0.2", "1.0.1")]
    [InlineData("v1.1.0", "1.0.9")]
    [InlineData("v2.0.0", "1.9.9")]
    // String comparison would call 1.0.9 newer than 1.0.10; version comparison must not.
    [InlineData("v1.0.10", "1.0.9")]
    public void IsNewer_TrueWhenTheTagIsAhead(string tag, string current)
        => Assert.True(VersionCompare.IsNewer(tag, current));

    [Theory]
    [InlineData("v1.0.1", "1.0.1")]
    [InlineData("v1.0.0", "1.0.1")]
    [InlineData("v0.9.9", "1.0.1")]
    public void IsNewer_FalseWhenTheTagIsNotAhead(string tag, string current)
        => Assert.False(VersionCompare.IsNewer(tag, current));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData("v")]
    [InlineData("release-2026-09-10")]
    [InlineData("v1.2.3-beta.1")]
    public void IsNewer_TreatsAnUnparseableTagAsNoUpdate(string? tag)
    {
        // Not an error. A tag we cannot read is upstream's problem, not something to alarm
        // a user about, and certainly not grounds for offering a download.
        Assert.False(VersionCompare.IsNewer(tag, "1.0.1"));
    }

    [Fact]
    public void IsNewer_FalseWhenTheCurrentVersionIsUnreadable()
    {
        // If we cannot tell what we are, we must not claim something else is newer.
        Assert.False(VersionCompare.IsNewer("v99.0.0", "not-a-version"));
    }

    [Fact]
    public void TryParseTag_StripsALeadingV()
        => Assert.Equal(new Version(1, 2, 3), VersionCompare.TryParseTag("v1.2.3"));

    [Fact]
    public void TryParseTag_AcceptsATwoPartVersion()
        => Assert.Equal(new Version(1, 2), VersionCompare.TryParseTag("1.2"));
}
