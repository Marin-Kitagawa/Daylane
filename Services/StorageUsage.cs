using Microsoft.Data.Sqlite;

namespace Daylane.Services;

/// <param name="DatabaseBytes">Null when the files cannot be measured. Reporting 0 for an
/// unreadable file would be a claim; null is the truth, and the UI says "unavailable".</param>
/// <param name="RecordedRows">Null when the row count cannot be read. Reporting 0 for a query
/// that failed would be a claim -- indistinguishable from a genuinely empty database -- so null
/// is the truth, and the UI says "unavailable"; a real 0 still means confirmed empty.</param>
internal sealed record StorageReport(long? DatabaseBytes, long? RecordedRows, string DatabasePath);

/// <summary>
/// How much disk the database occupies and how many rows it holds.
///
/// The size is the sum of the main file, the WAL and the shared-memory file, not just the main
/// file. Daylane runs in WAL mode and an un-checkpointed WAL can be larger than the database
/// itself, so reporting the main file alone would understate the real footprint -- sometimes
/// by more than it reports.
/// </summary>
internal static class StorageUsage
{
    private static readonly string[] CountedTables =
        ["ActivitySegment", "OpenAppSegment", "DailyInput"];

    internal static StorageReport Measure(string databasePath, string connectionString) =>
        new(MeasureBytes(databasePath), CountRows(connectionString), databasePath);

    private static long? MeasureBytes(string databasePath)
    {
        try
        {
            var main = new FileInfo(databasePath);
            if (!main.Exists)
            {
                return null;
            }

            long total = main.Length;
            foreach (string suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = new FileInfo(databasePath + suffix);
                if (sidecar.Exists)
                {
                    total += sidecar.Length;
                }
            }

            return total;
        }
        catch (Exception)
        {
            // Locked, denied, or a path we cannot stat. Unknown, not zero.
            return null;
        }
    }

    private static long? CountRows(string connectionString)
    {
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            long total = 0;
            foreach (string table in CountedTables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM {table};";
                total += (long)(command.ExecuteScalar() ?? 0L);
            }

            return total;
        }
        catch (Exception)
        {
            // Cannot open or query the database. Unknown, not zero -- a real empty database
            // still reports a real 0.
            return null;
        }
    }
}
