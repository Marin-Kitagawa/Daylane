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

        // 5 header rows + title rows accounted for, nothing extra or dropped.
        Assert.Equal(5, panel.Items.Count);
    }

    [Fact]
    public void Build_BrowserRowsWithNoHostAreGroupedUnderOther()
    {
        TitleUsageSummary[] rows = [Row("chrome://settings", TimeSpan.FromMinutes(3), urlHost: null)];

        var panel = TitleUsagePresenter.Build(rows, isBrowser: true);

        Assert.Equal(2, panel.Items.Count);
        Assert.True(panel.Items[0].IsHeader);
        Assert.Equal(TitleUsagePresenter.OtherHostLabel, panel.Items[0].DisplayText);
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
        Assert.False(panel.HasItems);
        Assert.Equal(TimeSpan.Zero, panel.TotalDuration);
    }
}
