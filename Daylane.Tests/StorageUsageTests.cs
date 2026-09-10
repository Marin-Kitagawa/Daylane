using Daylane.Models;
using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class StorageUsageTests
{
    private static ForegroundApp App(string process) =>
        new(process, $@"C:\Apps\{process}.exe", process, false);

    [Fact]
    public void Measure_ReportsTheDatabaseSizeAndItsPath()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        StorageReport report = StorageUsage.Measure(temp.DatabasePath, temp.ConnectionString);

        Assert.Equal(temp.DatabasePath, report.DatabasePath);
        Assert.NotNull(report.DatabaseBytes);
        Assert.True(report.DatabaseBytes > 0, "an opened database is not zero bytes");
    }

    [Fact]
    public void Measure_SumsTheMainWalAndShmFileLengths()
    {
        // Synthetic, hand-made files rather than a real database: nothing else touches them
        // between the writes and the measurement, so this pins the summing arithmetic itself
        // (a real WAL database can checkpoint on connection close and move bytes between
        // files mid-test, which is exactly what made the original version of this test racy).
        string directory = Path.Combine(Path.GetTempPath(), "daylane-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string databasePath = Path.Combine(directory, "x.db");
            const int mainLength = 517;
            const int walLength = 231;
            const int shmLength = 89;
            File.WriteAllBytes(databasePath, new byte[mainLength]);
            File.WriteAllBytes(databasePath + "-wal", new byte[walLength]);
            File.WriteAllBytes(databasePath + "-shm", new byte[shmLength]);

            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ConnectionString;

            StorageReport report = StorageUsage.Measure(databasePath, connectionString);

            Assert.Equal(mainLength + walLength + shmLength, report.DatabaseBytes);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup; a locked file here must not fail the assertion above.
            }
        }
    }

    [Fact]
    public void Measure_ReportsAtLeastTheMainFileLengthForARealStore()
    {
        // A ">=" comparison against a real, live store is deterministic even though closing a
        // WAL connection can checkpoint and move bytes from the sidecar files into the main
        // file between our read of it and the call to Measure: it holds either way, and it
        // still catches a regression that ignores the sidecar files or under-measures the
        // main file.
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);
        for (int i = 0; i < 40; i++)
        {
            long id = store.OpenSegment(App($"app{i}"), start.AddSeconds(i));
            store.CloseSegment(id, start.AddSeconds(i + 1), 1, 1);
        }
        store.Flush();

        long mainBytes = new FileInfo(temp.DatabasePath).Length;
        StorageReport report = StorageUsage.Measure(temp.DatabasePath, temp.ConnectionString);

        Assert.NotNull(report.DatabaseBytes);
        Assert.True(report.DatabaseBytes >= mainBytes,
            $"expected at least the main file's {mainBytes} bytes, got {report.DatabaseBytes}");
    }

    [Fact]
    public void Measure_CountsRecordedRows()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);
        long a = store.OpenSegment(App("chrome"), start);
        store.CloseSegment(a, start.AddMinutes(1), 0, 0);
        long b = store.OpenSegment(App("notepad"), start.AddMinutes(2));
        store.CloseSegment(b, start.AddMinutes(3), 0, 0);
        store.Flush();

        StorageReport report = StorageUsage.Measure(temp.DatabasePath, temp.ConnectionString);

        Assert.True(report.RecordedRows >= 2,
            $"two segments plus today's input row should count at least 2, got {report.RecordedRows}");
    }

    [Fact]
    public void Measure_ReportsUnknownRatherThanZeroForAMissingFile()
    {
        using var temp = new TempDatabase();
        string missing = Path.Combine(temp.DatabasePath + "-nope", "daylane.db");

        StorageReport report = StorageUsage.Measure(missing, temp.ConnectionString);

        // Zero is a claim. Unknown is the truth.
        Assert.Null(report.DatabaseBytes);
    }

    [Fact]
    public void Measure_ReportsZeroRowsAfterAPurge()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);
        store.CloseSegment(store.OpenSegment(App("chrome"), start), start.AddMinutes(1), 0, 0);
        store.Flush();
        store.PurgeRecordedActivity();

        Assert.Equal(0, StorageUsage.Measure(temp.DatabasePath, temp.ConnectionString).RecordedRows);
    }
}
