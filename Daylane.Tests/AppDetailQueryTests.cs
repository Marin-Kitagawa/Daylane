using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class AppDetailQueryTests
{
    private static ForegroundApp App(string process, string? title, string? host = null) =>
        new(process, $@"C:\Apps\{process}.exe", process, false) { WindowTitle = title, UrlHost = host };

    [Fact]
    public void GetTitleUsage_GroupsByTitleAndSumsDuration()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-10);

        long a = store.OpenSegment(App("chrome", "Inbox"), start);
        store.CloseSegment(a, start.AddMinutes(2), 0, 0);
        long b = store.OpenSegment(App("chrome", "Inbox"), start.AddMinutes(3));
        store.CloseSegment(b, start.AddMinutes(6), 0, 0);
        long c = store.OpenSegment(App("chrome", "Calendar"), start.AddMinutes(7));
        store.CloseSegment(c, start.AddMinutes(8), 0, 0);

        var (rangeStart, rangeEnd) = TestRanges.UnambiguousRangeAround(start);
        var rows = store.GetTitleUsage(@"C:\Apps\chrome.exe", rangeStart, rangeEnd);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Inbox", rows[0].Title);
        Assert.Equal(2, rows[0].SessionCount);
        Assert.Equal(TimeSpan.FromMinutes(5), rows[0].Duration);
        Assert.Equal("Calendar", rows[1].Title);
    }

    [Fact]
    public void GetTitleUsage_MarksExcludedRowsWithoutHidingThem()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);

        long id = store.OpenSegment(App("chrome", "Ignored"), start, excluded: true);
        store.CloseSegment(id, start.AddMinutes(1), 0, 0);

        var (rangeStart, rangeEnd) = TestRanges.UnambiguousRangeAround(start);
        var rows = store.GetTitleUsage(@"C:\Apps\chrome.exe", rangeStart, rangeEnd);

        // Hiding excluded rows entirely would make the rule that excluded them undiscoverable.
        Assert.Single(rows);
        Assert.True(rows[0].IsExcluded);
    }

    [Fact]
    public void GetTitleUsage_CarriesTheHostForBrowserRows()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);

        long id = store.OpenSegment(App("chrome", "Inbox", "mail.example.com"), start);
        store.CloseSegment(id, start.AddMinutes(1), 0, 0);

        var (rangeStart, rangeEnd) = TestRanges.UnambiguousRangeAround(start);
        var rows = store.GetTitleUsage(@"C:\Apps\chrome.exe", rangeStart, rangeEnd);

        Assert.Equal("mail.example.com", rows[0].UrlHost);
    }

    [Fact]
    public void GetTitleUsage_IgnoresSegmentsForOtherExePaths()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);

        long chrome = store.OpenSegment(App("chrome", "Inbox"), start);
        store.CloseSegment(chrome, start.AddMinutes(1), 0, 0);
        long other = store.OpenSegment(App("notepad", "Untitled"), start);
        store.CloseSegment(other, start.AddMinutes(1), 0, 0);

        var (rangeStart, rangeEnd) = TestRanges.UnambiguousRangeAround(start);
        var rows = store.GetTitleUsage(@"C:\Apps\chrome.exe", rangeStart, rangeEnd);

        Assert.Single(rows);
        Assert.Equal("Inbox", rows[0].Title);
    }

    [Fact]
    public void GetTitleUsage_ClipsOpenSegmentDurationToRangeEnd()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-3);

        // Left open (no CloseSegment): duration must come from EffectiveEndUtc/now-clamp, the
        // same open-segment handling AggregateAppUsage owns, not a second reimplementation of it.
        store.OpenSegment(App("chrome", "Inbox"), start);

        var (rangeStart, rangeEnd) = TestRanges.UnambiguousRangeAround(start);
        var rows = store.GetTitleUsage(@"C:\Apps\chrome.exe", rangeStart, rangeEnd);

        Assert.Single(rows);
        Assert.True(rows[0].Duration >= TimeSpan.FromMinutes(2) && rows[0].Duration <= TimeSpan.FromMinutes(4));
    }
}
