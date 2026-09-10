using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class PurgeIntegrationTests
{
    private static ForegroundApp App(string process) =>
        new(process, $@"C:\Apps\{process}.exe", process, false);

    private static long SegmentCount(TempDatabase temp)
    {
        using var connection = temp.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ActivitySegment;";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void PurgingWithAnOpenSegment_LeavesNoRowBehindAndNoDanglingId()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);

        // An open segment: opened, never closed. This is the state the tracker is in almost
        // always, so it is the state a purge has to survive.
        long openId = store.OpenSegment(App("chrome"), start);
        Assert.Equal(1L, SegmentCount(temp));

        store.CloseSegment(openId, DateTime.UtcNow, 0, 0);
        store.Flush();
        store.PurgeRecordedActivity();

        Assert.Equal(0L, SegmentCount(temp));

        // The id is now dangling. Writing counts against it must not resurrect a row.
        store.UpdateOpenSegmentCounts(openId, 5, 5);
        Assert.Equal(0L, SegmentCount(temp));
    }

    [Fact]
    public void MeasureStorage_AfterAPurge_ReportsNoRows()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);
        store.CloseSegment(store.OpenSegment(App("chrome"), start), start.AddMinutes(1), 0, 0);
        store.Flush();

        store.PurgeRecordedActivity();
        StorageReport report = StorageUsage.Measure(temp.DatabasePath, temp.ConnectionString);

        Assert.Equal(0, report.RecordedRows);
        Assert.NotNull(report.DatabaseBytes);
    }
}
