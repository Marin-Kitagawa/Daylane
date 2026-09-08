using Microsoft.Data.Sqlite;

namespace Daylane.Services;

/// <summary>
/// Drops capture data older than the retention window. These rows are this device's own
/// recordings rather than shared entities, so they are deleted outright: there is no peer
/// that needs a tombstone to learn about the deletion.
/// </summary>
internal static class RetentionPruner
{
    internal static int Prune(SqliteConnection connection, int retentionDays, DateTime todayLocal)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        string cutoff = todayLocal.Date.AddDays(-retentionDays).ToString("yyyy-MM-dd");
        int deleted = 0;

        using var transaction = connection.BeginTransaction();
        foreach (string table in (string[])["ActivitySegment", "OpenAppSegment"])
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE LocalDate < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", cutoff);
            deleted += command.ExecuteNonQuery();
        }

        using (var inputs = connection.CreateCommand())
        {
            inputs.Transaction = transaction;
            inputs.CommandText = "DELETE FROM DailyInput WHERE LogDate < $cutoff;";
            inputs.Parameters.AddWithValue("$cutoff", cutoff);
            deleted += inputs.ExecuteNonQuery();
        }

        transaction.Commit();
        return deleted;
    }
}
