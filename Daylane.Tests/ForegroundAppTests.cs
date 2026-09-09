using Daylane.Models;

namespace Daylane.Tests;

public class ForegroundAppTests
{
    private static ForegroundApp App(string process, string? title = null) =>
        new(process, $@"C:\Apps\{process}.exe", process, false) { WindowTitle = title };

    [Fact]
    public void SameIdentity_IgnoresTheWindowTitle()
    {
        // A title change must NOT open a new segment: browser tab switches would otherwise
        // multiply rows for no analytical gain.
        Assert.True(App("chrome", "Inbox").SameIdentity(App("chrome", "Calendar")));
    }

    [Fact]
    public void SameIdentity_StillDistinguishesProcesses()
        => Assert.False(App("chrome", "x").SameIdentity(App("firefox", "x")));

    [Fact]
    public void WindowTitle_DefaultsToNull()
        => Assert.Null(new ForegroundApp("chrome", @"C:\chrome.exe", "chrome", false).WindowTitle);

    [Fact]
    public void UrlHost_DefaultsToNull()
        => Assert.Null(new ForegroundApp("chrome", @"C:\chrome.exe", "chrome", false).UrlHost);

    [Fact]
    public void WithExpression_ReplacesTitleWithoutChangingIdentity()
    {
        ForegroundApp original = App("chrome", "Inbox");
        ForegroundApp retitled = original with { WindowTitle = "Calendar" };

        Assert.Equal("Calendar", retitled.WindowTitle);
        Assert.True(original.SameIdentity(retitled));
    }
}
