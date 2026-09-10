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
    public void License_IsExactlyTheRepositorysLicence()
    {
        // The version is the legally meaningful part: AGPL-3.0 and AGPL-1.0 are different
        // licences. Contains("AGPL") would accept either.
        Assert.Equal("AGPL-3.0", AppInfo.License);
    }

    [Fact]
    public void RepositoryUrl_PointsAtThisFork()
        => Assert.Equal("https://github.com/Marin-Kitagawa/Daylane", AppInfo.RepositoryUrl);

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
    public void Author_IsTheDeclaredAuthor()
    {
        // Pinned rather than merely non-empty: a wrong value here is a false attribution
        // shown in the About panel, and changing it should be a deliberate edit.
        Assert.Equal("Marin Kitagawa", AppInfo.Author);
    }
}
