using Daylane.Models;
using Daylane.Services;
using Microsoft.Data.Sqlite;

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

    /// <summary>
    /// The purge must actually give the bytes back, measured the way the Data panel measures
    /// them -- StorageUsage.Measure over main + WAL + shm, on the real store.
    ///
    /// This is the test the sub-project shipped without, and the gap it left was real: Daylane
    /// runs in WAL mode, where VACUUM rebuilds the database *into the WAL* and neither file
    /// shrinks until a checkpoint runs. VACUUM alone therefore returned success and freed
    /// nothing observable, so RefreshStorage redrew the same size beside "Deleted N rows."
    ///
    /// Two details make the measurement mean something rather than merely pass:
    ///
    /// - The pool is cleared before the *before* measurement. That closes the last real handle,
    ///   which makes SQLite checkpoint the seed data into the main file, so "before" is a
    ///   settled number rather than a WAL that a later checkpoint would have folded away.
    /// - The pool is deliberately NOT cleared before the *after* measurement. SQLite checkpoints
    ///   on its own when the last connection to a WAL database closes, so clearing the pool
    ///   there would do the purge's job for it and grant a pass to code that never checkpoints.
    ///   Leaving the pooled connections open is also the production state: the app is running,
    ///   the store is live, and nothing is about to close the database.
    /// </summary>
    [Fact]
    public void PurgingReclaimsDiskSpace_AsTheDataPanelMeasuresIt()
    {
        const int segments = 2_500;

        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddDays(-1);

        for (int i = 0; i < segments; i++)
        {
            long id = store.OpenSegment(App($"app{i % 40}"), start.AddSeconds(i * 2));
            store.CloseSegment(id, start.AddSeconds((i * 2) + 1), i, i);
        }

        store.Flush();
        SqliteConnection.ClearAllPools();

        long? before = StorageUsage.Measure(temp.DatabasePath, temp.ConnectionString).DatabaseBytes;
        Assert.NotNull(before);

        store.PurgeRecordedActivity();

        long? after = StorageUsage.Measure(temp.DatabasePath, temp.ConnectionString).DatabaseBytes;
        Assert.NotNull(after);

        Assert.True(after < before,
            $"the purge must return bytes to the filesystem: before={before:N0} after={after:N0}");
    }
}
