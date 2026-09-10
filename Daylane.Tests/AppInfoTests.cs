using Daylane.Services;

namespace Daylane.Tests;

public class AppInfoTests
{
    [Fact]
    public void Version_IsAReadableVersionNotAPlaceholder()
    {
        // Reads the real assembly, so this fails if the informational version stops resolving.
        Assert.NotNull(VersionCompare.TryParseTag(AppInfo.Version));
    }

    [Fact]
    public void License_IsTheRepositorysOwnLicence()
    {
        // AGPL-3.0, not the MIT of the project whose feature set this port follows.
        Assert.Contains("AGPL", AppInfo.License, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://github.com/Marin-Kitagawa/Daylane")]
    public void RepositoryUrl_PointsAtThisFork(string expected)
        => Assert.Equal(expected, AppInfo.RepositoryUrl);

    [Fact]
    public void EveryLink_IsAnAbsoluteHttpsUrl()
    {
        foreach (string url in new[]
                 {
                     AppInfo.RepositoryUrl, AppInfo.IssuesUrl,
                     AppInfo.LicenseUrl, AppInfo.UpstreamProjectUrl
                 })
        {
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed), url);
            Assert.Equal(Uri.UriSchemeHttps, parsed!.Scheme);
        }
    }

    [Fact]
    public void Author_IsNotEmpty()
        => Assert.False(string.IsNullOrWhiteSpace(AppInfo.Author));
}
