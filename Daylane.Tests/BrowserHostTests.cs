using Daylane.Services;

namespace Daylane.Tests;

public class BrowserHostTests
{
    [Theory]
    [InlineData("chrome")]
    [InlineData("Chrome")]
    [InlineData("msedge")]
    [InlineData("firefox")]
    [InlineData("brave")]
    public void IsBrowser_RecognisesKnownBrowsers(string process)
        => Assert.True(BrowserHost.IsBrowser(process));

    [Theory]
    [InlineData("devenv")]
    [InlineData("WindowsTerminal")]
    [InlineData("")]
    public void IsBrowser_RejectsEverythingElse(string process)
        => Assert.False(BrowserHost.IsBrowser(process));

    [Theory]
    [InlineData("https://mail.example.com/u/0/inbox?q=secret", "mail.example.com")]
    [InlineData("http://example.com", "example.com")]
    [InlineData("https://example.com:8443/path", "example.com")]
    [InlineData("https://sub.domain.example.co.uk/x", "sub.domain.example.co.uk")]
    public void HostFromUrl_KeepsOnlyTheHost(string url, string expected)
        => Assert.Equal(expected, BrowserHost.HostFromUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url at all")]
    [InlineData("just some window title")]
    public void HostFromUrl_ReturnsNullForJunk(string? candidate)
        => Assert.Null(BrowserHost.HostFromUrl(candidate));

    [Fact]
    public void HostFromUrl_NeverLeaksThePath()
    {
        string? host = BrowserHost.HostFromUrl("https://example.com/very/secret/path?token=abc");

        Assert.Equal("example.com", host);
        Assert.DoesNotContain("secret", host);
        Assert.DoesNotContain("token", host);
    }
}
