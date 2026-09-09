using Daylane.Services;

namespace Daylane.Tests;

public class PrivacyKeywordsTests
{
    [Fact]
    public void NoKeywords_SuppressesNothing()
        => Assert.False(PrivacyKeywords.Suppresses("anything", "example.com", Array.Empty<string>()));

    [Fact]
    public void TitleMatch_Suppresses()
        => Assert.True(PrivacyKeywords.Suppresses("My Bank - Login", null, new[] { "bank" }));

    [Fact]
    public void HostMatch_Suppresses()
        => Assert.True(PrivacyKeywords.Suppresses(null, "secure.mybank.com", new[] { "mybank" }));

    [Fact]
    public void Match_IsCaseInsensitive()
        => Assert.True(PrivacyKeywords.Suppresses("PASSWORD manager", null, new[] { "password" }));

    [Fact]
    public void HostMatch_SuppressesEvenWhenTheTitleDoesNotMatch()
    {
        // The title must be non-null and non-matching, so this can only pass if control
        // falls through the title check and actually reaches the host check.
        Assert.True(PrivacyKeywords.Suppresses("Daylane", "secure.mybank.com", new[] { "bank" }));
    }

    [Fact]
    public void HostMatch_IsCaseInsensitive()
        => Assert.True(PrivacyKeywords.Suppresses(null, "secure.MYBANK.com", new[] { "mybank" }));

    [Fact]
    public void NoMatch_DoesNotSuppress()
        => Assert.False(PrivacyKeywords.Suppresses("Daylane", "github.com", new[] { "bank" }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceKeyword_IsIgnored(string keyword)
    {
        // Same hazard as ignore rules: Contains("") is always true, so an empty keyword
        // would suppress capture for every single segment.
        Assert.False(PrivacyKeywords.Suppresses("any title", "any.host", new[] { keyword }));
    }

    [Fact]
    public void BothNull_DoesNotSuppress()
        => Assert.False(PrivacyKeywords.Suppresses(null, null, new[] { "bank" }));
}
