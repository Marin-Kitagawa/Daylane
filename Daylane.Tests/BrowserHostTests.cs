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
    // Both parse as absolute URIs with an empty Host. Returning "" instead of null would count
    // as a hit and stop the walk before it reached the address bar, so this is not cosmetic.
    [InlineData("about:blank")]
    [InlineData("file:///C:/x/secret.txt")]
    public void HostFromUrl_ReturnsNullForJunk(string? candidate)
        => Assert.Null(BrowserHost.HostFromUrl(candidate));

    [Fact]
    public void HostFromUrl_RejectsHostsLongerThanADnsName()
    {
        // Parses fine, but is a crafted authority rather than a site: without a cap this would
        // be persisted verbatim, once per segment, to an uncapped TEXT column.
        string longHost = string.Join('.', Enumerable.Repeat("aaa", 60_000));

        Assert.True(longHost.Length > 253);
        Assert.Null(BrowserHost.HostFromUrl($"https://{longHost}/"));
    }

    [Fact]
    public void HostFromUrl_KeepsAHostAtTheDnsLengthLimit()
    {
        // 253 characters exactly: the boundary must be inclusive, or real long hostnames break.
        string host = string.Join('.', Enumerable.Repeat("aaaa", 50)) + ".aaa";

        Assert.Equal(253, host.Length);
        Assert.Equal(host, BrowserHost.HostFromUrl($"https://{host}/path"));
    }

    [Fact]
    public void HostFromUrl_NeverLeaksThePath()
    {
        string? host = BrowserHost.HostFromUrl("https://example.com/very/secret/path?token=abc");

        Assert.Equal("example.com", host);
        Assert.DoesNotContain("secret", host);
        Assert.DoesNotContain("token", host);
    }
}
