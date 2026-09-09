using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class CaptureDefaultsTests
{
    [Fact]
    public void WithDefaultSettings_TitleCaptureIsOff()
    {
        using var temp = new TempDatabase();
        using (var connection = temp.Open())
        {
            Migrations.Apply(connection, temp.DatabasePath);
        }

        var settings = new SettingsService(temp.ConnectionString).Current;

        // The promise: an upgraded install that changes nothing records what it recorded before.
        Assert.False(settings.RecordWindowTitles);
        Assert.False(settings.RecordBrowserHost);
    }

    [Fact]
    public void CapturePolicy_WithTitlesOff_StripsTitleAndHost()
    {
        var app = new ForegroundApp("chrome", @"C:\chrome.exe", "chrome", false)
        {
            WindowTitle = "Inbox",
            UrlHost = "mail.example.com"
        };

        var settings = new DaylaneSettings().Normalize();
        Assert.False(settings.RecordWindowTitles);

        // Drives the real production policy. An earlier draft re-implemented the branch
        // inline, which would have passed even if CapturePolicy were deleted outright.
        ForegroundApp stored = CapturePolicy.Apply(app, settings, out bool excluded);

        Assert.Null(stored.WindowTitle);
        Assert.Null(stored.UrlHost);
        Assert.False(excluded);
    }
}
