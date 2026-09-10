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
        // always, so it is the state a purge has to survive. Task 7's own seeds all go through
        // OpenSegment *and* CloseSegment before purging, so a still-open row is a case none of
        // those tests touch.
        store.OpenSegment(App("chrome"), start);
        Assert.Equal(1L, SegmentCount(temp));

        store.PurgeRecordedActivity();

        Assert.Equal(0L, SegmentCount(temp));
    }
}
