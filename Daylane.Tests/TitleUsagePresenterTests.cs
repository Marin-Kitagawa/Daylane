using Daylane.Models;
using Daylane.ViewModels;

namespace Daylane.Tests;

/// <summary>
/// Covers TitleUsagePresenter.Build, the pure helper Task 13 pulled out of
/// MainWindowViewModel for exactly this reason: MainWindowViewModel cannot be constructed in a
/// unit test (its TrackingService opens a real database next to the executable), so the two
/// rules that can silently regress on screen -- the panel total excluding excluded rows, and
/// browser apps grouping by host while everything else stays flat -- are pinned here instead.
/// </summary>
public class TitleUsagePresenterTests
{
    private static TitleUsageSummary Row(
        string? title,
        TimeSpan duration,
        int sessionCount = 1,
        bool isExcluded = false,
        string? urlHost = null) =>
        new()
        {
            Title = title,
            UrlHost = urlHost,
            Duration = duration,
            SessionCount = sessionCount,
            IsExcluded = isExcluded
        };

    [Fact]
    public void Build_ExcludedRowsAreVisibleButDoNotContributeToTheTotal()
    {
        TitleUsageSummary[] rows =
        [
            Row("Inbox", TimeSpan.FromMinutes(10)),
            Row("Ignored Tab", TimeSpan.FromMinutes(30), isExcluded: true)
        ];

        var panel = TitleUsagePresenter.Build(rows, isBrowser: false);

        // Both rows still show up on screen -- hiding the excluded one would make the rule
        // that excluded it undiscoverable.
        Assert.Equal(2, panel.Items.Count);
        Assert.Contains(panel.Items, i => i.DisplayText == "Ignored Tab" && i.IsExcluded);

        // But its 30 minutes must not appear in the total: only the non-excluded row counts.
        Assert.Equal(TimeSpan.FromMinutes(10), panel.TotalDuration);
    }

    [Fact]
    public void Build_BrowserAppGroupsByHostWithTitlesNested()
    {
        TitleUsageSummary[] rows =
        [
            Row("Inbox", TimeSpan.FromMinutes(5), urlHost: "mail.example.com"),
            Row("Calendar", TimeSpan.FromMinutes(2), urlHost: "mail.example.com"),
            Row("Docs Home", TimeSpan.FromMinutes(1), urlHost: "docs.example.com")
        ];

        var panel = TitleUsagePresenter.Build(rows, isBrowser: true);

        // A header row per distinct host, with that host's titles nested immediately after it.
        var mailHeaderIndex = panel.Items.ToList().FindIndex(i => i.IsHeader && i.DisplayText == "mail.example.com");
        var docsHeaderIndex = panel.Items.ToList().FindIndex(i => i.IsHeader && i.DisplayText == "docs.example.com");
        Assert.True(mailHeaderIndex >= 0);
        Assert.True(docsHeaderIndex >= 0);

        Assert.True(panel.Items[mailHeaderIndex + 1].IsNested);
        Assert.Equal("Inbox", panel.Items[mailHeaderIndex + 1].DisplayText);
        Assert.True(panel.Items[mailHeaderIndex + 2].IsNested);
        Assert.Equal("Calendar", panel.Items[mailHeaderIndex + 2].DisplayText);

        Assert.True(panel.Items[docsHeaderIndex + 1].IsNested);
        Assert.Equal("Docs Home", panel.Items[docsHeaderIndex + 1].DisplayText);

        // 2 headers + 3 titles accounted for, nothing extra or dropped.
        Assert.Equal(5, panel.Items.Count);
    }

    [Fact]
    public void Build_BrowserRowsWithNoHostAreGroupedUnderOther()
    {
        TitleUsageSummary[] rows = [Row("chrome://settings", TimeSpan.FromMinutes(3), urlHost: null)];

        var panel = TitleUsagePresenter.Build(rows, isBrowser: true);

        Assert.Equal(2, panel.Items.Count);
        Assert.True(panel.Items[0].IsHeader);
        Assert.Equal("Other", panel.Items[0].DisplayText);
        Assert.True(panel.Items[1].IsNested);
        Assert.Equal("chrome://settings", panel.Items[1].DisplayText);
    }

    [Fact]
    public void Build_NonBrowserAppProducesAFlatTitleListWithNoHeaders()
    {
        TitleUsageSummary[] rows =
        [
            Row("Quarterly Budget Review", TimeSpan.FromMinutes(20)),
            Row("Untitled Document", TimeSpan.FromMinutes(4))
        ];

        var panel = TitleUsagePresenter.Build(rows, isBrowser: false);

        Assert.Equal(2, panel.Items.Count);
        Assert.All(panel.Items, i => Assert.False(i.IsHeader));
        Assert.All(panel.Items, i => Assert.False(i.IsNested));
        Assert.Equal("Quarterly Budget Review", panel.Items[0].DisplayText);
        Assert.Equal("Untitled Document", panel.Items[1].DisplayText);
    }

    [Fact]
    public void Build_EmptyRowsProduceTheEmptyStateNotAZeroRowList()
    {
        var panel = TitleUsagePresenter.Build([], isBrowser: false);

        Assert.Empty(panel.Items);
        Assert.Equal(TimeSpan.Zero, panel.TotalDuration);
    }

    [Fact]
    public void Build_BrowserHostHeadersCarryTheirOwnCountedTime()
    {
        TitleUsageSummary[] rows =
        [
            Row("Inbox", TimeSpan.FromMinutes(5), urlHost: "mail.example.com"),
            Row("Spam", TimeSpan.FromMinutes(9), urlHost: "mail.example.com", isExcluded: true),
            Row("Docs Home", TimeSpan.FromMinutes(2), urlHost: "docs.example.com")
        ];

        var panel = TitleUsagePresenter.Build(rows, isBrowser: true);

        // Time per site is the point of grouping by host, and the groups are already ordered by
        // it -- so the header shows it rather than leaving the ordering key invisible. Excluded
        // rows are out of it, exactly as they are out of the panel total, which keeps the
        // headers summing to that total.
        var mail = panel.Items.Single(i => i.IsHeader && i.DisplayText == "mail.example.com");
        var docs = panel.Items.Single(i => i.IsHeader && i.DisplayText == "docs.example.com");
        Assert.Equal("5m 00s", mail.DurationText);
        Assert.Equal("2m 00s", docs.DurationText);
        Assert.Equal(TimeSpan.FromMinutes(7), panel.TotalDuration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_WithNoTitleOrHostOnAnyRow_ProducesTheEmptyStateNotAnUntitledRow(bool isBrowser)
    {
        // The default configuration: titles off, so every segment stored a null title and a null
        // host and GetTitleUsage returns exactly one (null, null) group with the app's whole
        // duration. Rendering it gave a fresh user a group called "Other" holding a row called
        // "(untitled)", and made the "No titles captured" empty state unreachable -- the panel
        // always had one item, so HasSelectedAppTitles was always true.
        TitleUsageSummary[] rows = [Row(null, TimeSpan.FromMinutes(10))];

        var panel = TitleUsagePresenter.Build(rows, isBrowser);

        Assert.Empty(panel.Items);
    }

    [Fact]
    public void Build_KeepsADetaillessRowWhenAnotherRowHasATitle()
    {
        // Not the same case: with titles on, a row whose own title was suppressed by a privacy
        // keyword is real information about where the time went, so it must not be dropped.
        TitleUsageSummary[] rows =
        [
            Row("Inbox", TimeSpan.FromMinutes(5)),
            Row(null, TimeSpan.FromMinutes(3))
        ];

        var panel = TitleUsagePresenter.Build(rows, isBrowser: false);

        Assert.Equal(2, panel.Items.Count);
        Assert.Contains(panel.Items, i => i.DisplayText == "(untitled)");
        Assert.Equal(TimeSpan.FromMinutes(8), panel.TotalDuration);
    }

    [Theory]
    [InlineData(false, @"C:\Apps\chrome.exe", false)]
    [InlineData(false, "", true)]
    [InlineData(false, null, true)]
    [InlineData(true, "", true)]
    public void DetailUnavailable_IsTrueOnlyWhenNoBreakdownCouldEverExist(
        bool isIdle,
        string? exePath,
        bool expected)
    {
        // An app with no resolved program file (elevated or protected) and the synthetic "Away"
        // row cannot have a breakdown whatever the privacy settings say. Telling that user to
        // "turn on title capture in Settings" is advice that cannot work, so the drawer needs
        // this apart from the titles-are-off case.
        Assert.Equal(expected, TitleUsagePresenter.DetailUnavailable(isIdle, exePath));
    }
}
