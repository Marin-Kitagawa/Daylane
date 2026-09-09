using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class TitleSearchTests
{
    private static ForegroundApp App(string process, string? title, string? host = null) =>
        new(process, $@"C:\Apps\{process}.exe", process, false) { WindowTitle = title, UrlHost = host };

    private static DailyStatsStore Seed(TempDatabase temp, out DateTime start)
    {
        var store = new DailyStatsStore(temp.DatabasePath);
        start = DateTime.UtcNow.AddMinutes(-20);
        long a = store.OpenSegment(App("chrome", "Quarterly Budget Review"), start);
        store.CloseSegment(a, start.AddMinutes(5), 0, 0);
        long b = store.OpenSegment(App("chrome", "Inbox", "mail.example.com"), start.AddMinutes(6));
        store.CloseSegment(b, start.AddMinutes(9), 0, 0);
        long c = store.OpenSegment(App("devenv", null), start.AddMinutes(10));
        store.CloseSegment(c, start.AddMinutes(12), 0, 0);
        return store;
    }

    [Fact]
    public void SearchTitles_MatchesOnTitleSubstringCaseInsensitively()
    {
        using var temp = new TempDatabase();
        using var store = Seed(temp, out DateTime start);
        var (rangeStart, rangeEnd) = TestRanges.UnambiguousRangeAround(start);

        var hits = store.SearchTitles("budget", rangeStart, rangeEnd);

        Assert.Single(hits);
        Assert.Equal("Quarterly Budget Review", hits[0].WindowTitle);
    }

    [Fact]
    public void SearchTitles_MatchesOnHost()
    {
        using var temp = new TempDatabase();
        using var store = Seed(temp, out DateTime start);
        var (rangeStart, rangeEnd) = TestRanges.UnambiguousRangeAround(start);

        var hits = store.SearchTitles("mail.example", rangeStart, rangeEnd);

        Assert.Single(hits);
        Assert.Equal("Inbox", hits[0].WindowTitle);
    }

    [Fact]
    public void SearchTitles_IgnoresRowsWithNoTitle()
    {
        using var temp = new TempDatabase();
        using var store = Seed(temp, out DateTime start);
        var (rangeStart, rangeEnd) = TestRanges.UnambiguousRangeAround(start);

        var hits = store.SearchTitles("devenv", rangeStart, rangeEnd);

        Assert.Empty(hits);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SearchTitles_WithBlankQuery_ReturnsNothing(string query)
    {
        using var temp = new TempDatabase();
        using var store = Seed(temp, out DateTime start);
        var (rangeStart, rangeEnd) = TestRanges.UnambiguousRangeAround(start);

        // A blank query must not degenerate into "match every row".
        Assert.Empty(store.SearchTitles(query, rangeStart, rangeEnd));
    }

    [Fact]
    public void SearchTitles_EscapesWildcards()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);
        long id = store.OpenSegment(App("chrome", "plain title"), start);
        store.CloseSegment(id, start.AddMinutes(1), 0, 0);
        var (rangeStart, rangeEnd) = TestRanges.UnambiguousRangeAround(start);

        // "%" must be a literal, not "match anything".
        Assert.Empty(store.SearchTitles("%", rangeStart, rangeEnd));
    }
}
