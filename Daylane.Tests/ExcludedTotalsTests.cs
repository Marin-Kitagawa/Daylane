using Daylane.Models;
using Daylane.Services;
using Daylane.ViewModels;

namespace Daylane.Tests;

/// <summary>
/// The other half of ExcludedRecomputeTests. Those assert on the Excluded COLUMN -- that the
/// right rows get marked and unmarked -- which is why they all still passed while no aggregate
/// filtered on the column at all: "Excluded" was written, recomputed, indexed and badged, and
/// a 10-minute excluded segment still came back as a full 10 minutes of usage. Everything here
/// asserts on a TOTAL instead, which is the thing the spec ("Counted in reports? No"), the
/// README and the Settings panel all actually promise.
/// </summary>
public class ExcludedTotalsTests
{
    private static ForegroundApp App(string process, string? title = null) =>
        new(process, $@"C:\Apps\{process}.exe", process, false) { WindowTitle = title };

    private static ActivitySegment Segment(string process, DateTime startUtc, TimeSpan length, bool excluded) =>
        new()
        {
            Id = 0,
            StartUtc = startUtc,
            EndUtc = startUtc + length,
            ProcessName = process,
            ExePath = $@"C:\Apps\{process}.exe",
            DisplayName = process,
            IsIdle = false,
            Excluded = excluded
        };

    [Fact]
    public void AggregateAppUsage_LeavesOutAnAppWhoseTimeIsEntirelyExcluded()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-30);

        long counted = store.OpenSegment(App("devenv"), start);
        store.CloseSegment(counted, start.AddMinutes(10), 0, 0);
        long ignored = store.OpenSegment(App("Solitaire"), start.AddMinutes(10), excluded: true);
        store.CloseSegment(ignored, start.AddMinutes(20), 0, 0);

        var (rangeStart, rangeEnd) = TestRanges.UnambiguousRangeAround(start);
        var usage = store.AggregateAppUsage(
            store.GetSegmentsForLocalRange(rangeStart, rangeEnd),
            rangeStart,
            rangeEnd);

        // Ten minutes of Solitaire under an ignore rule used to come back as
        // AppUsageSummary { DisplayName = "Solitaire", Duration = 00:10:00 }.
        Assert.DoesNotContain(usage, a => a.DisplayName == "Solitaire");
        Assert.Equal(TimeSpan.FromMinutes(10), Assert.Single(usage).Duration);
    }

    [Fact]
    public void AggregateAppUsage_SubtractsOnlyTheExcludedPartOfAnAppsTime()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-30);

        // One app, one ignored window (a terminal running an overnight download) and one that
        // counts: the app keeps its row, minus the ignored window's time.
        long working = store.OpenSegment(App("WindowsTerminal", "editing a file"), start);
        store.CloseSegment(working, start.AddMinutes(6), 0, 0);
        long downloading = store.OpenSegment(
            App("WindowsTerminal", "npm download running"),
            start.AddMinutes(6),
            excluded: true);
        store.CloseSegment(downloading, start.AddMinutes(20), 0, 0);

        var (rangeStart, rangeEnd) = TestRanges.UnambiguousRangeAround(start);
        var usage = store.AggregateAppUsage(
            store.GetSegmentsForLocalRange(rangeStart, rangeEnd),
            rangeStart,
            rangeEnd);

        Assert.Equal(TimeSpan.FromMinutes(6), Assert.Single(usage).Duration);
    }

    [Fact]
    public void BuildDailyActiveMinutes_DoesNotCountExcludedTime()
    {
        DateTime dayLocal = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Local);
        DateTime startUtc = dayLocal.AddHours(9).ToUniversalTime();
        ActivitySegment[] segments =
        [
            Segment("devenv", startUtc, TimeSpan.FromMinutes(10), excluded: false),
            Segment("Solitaire", startUtc.AddMinutes(10), TimeSpan.FromMinutes(50), excluded: true)
        ];

        var points = TrackingService.BuildDailyActiveMinutes(segments, dayLocal, dayLocal);

        Assert.Equal(10, Assert.Single(points).ActiveMinutes, precision: 6);
    }

    [Fact]
    public void BuildHourlyActiveMinutes_DoesNotCountExcludedTime()
    {
        DateTime dayLocal = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Local);
        DateTime startUtc = dayLocal.AddHours(9).ToUniversalTime();
        ActivitySegment[] segments =
        [
            Segment("devenv", startUtc, TimeSpan.FromMinutes(10), excluded: false),
            Segment("Solitaire", startUtc.AddMinutes(10), TimeSpan.FromMinutes(20), excluded: true)
        ];

        double[] hours = MainWindowViewModel.BuildHourlyActiveMinutes(segments, dayLocal);

        Assert.Equal(10, hours[9], precision: 6);
        Assert.Equal(0, hours.Sum() - hours[9], precision: 6);
    }

    [Fact]
    public void GetTitleUsage_KeepsTheExcludedRowVisibleWhileTheTotalLeavesItOut()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-30);

        long counted = store.OpenSegment(App("chrome", "Inbox"), start);
        store.CloseSegment(counted, start.AddMinutes(4), 0, 0);
        long ignored = store.OpenSegment(App("chrome", "Ignored Tab"), start.AddMinutes(4), excluded: true);
        store.CloseSegment(ignored, start.AddMinutes(20), 0, 0);

        var (rangeStart, rangeEnd) = TestRanges.UnambiguousRangeAround(start);
        var rows = store.GetTitleUsage(@"C:\Apps\chrome.exe", rangeStart, rangeEnd);

        // Present, badged, and carrying its real duration on the row: dropping it would make the
        // rule that excluded it undiscoverable, which is why this one reader asks for it.
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Title == "Ignored Tab" && r.IsExcluded);

        // And absent from every total computed over those rows.
        Assert.Equal(TimeSpan.FromMinutes(4), TitleUsagePresenter.Build(rows, isBrowser: false).TotalDuration);

        var usage = store.AggregateAppUsage(
            store.GetSegmentsForLocalRange(rangeStart, rangeEnd),
            rangeStart,
            rangeEnd);
        Assert.Equal(TimeSpan.FromMinutes(4), Assert.Single(usage).Duration);
    }

    [Fact]
    public void SegmentWindows_DropsExcludedSegmentsUnlessAskedForThem()
    {
        DateTime now = DateTime.UtcNow;
        DateTime start = now.AddHours(-2);
        ActivitySegment[] segments =
        [
            Segment("devenv", start, TimeSpan.FromMinutes(10), excluded: false),
            Segment("Solitaire", start.AddMinutes(10), TimeSpan.FromMinutes(10), excluded: true)
        ];

        // The default is what every total gets: this is the by-construction guarantee, so a new
        // aggregate inherits the exclusion rule instead of having to remember it.
        var counted = SegmentWindows.Counted(segments, start.AddHours(-1), now, now).ToList();
        Assert.Equal("devenv", Assert.Single(counted).Segment.ProcessName);

        var everything = SegmentWindows
            .Counted(segments, start.AddHours(-1), now, now, includeExcluded: true)
            .ToList();
        Assert.Equal(2, everything.Count);
    }
}
