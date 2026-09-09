using Microsoft.Data.Sqlite;

namespace Daylane.Services;

/// <summary>
/// Resolves this machine's device id. Schema v2 defaults DeviceId to the literal 'local'
/// because migration SQL cannot mint a UUID; this promotes those rows to a real id once.
/// </summary>
internal static class DeviceIdentity
{
    internal const string Placeholder = "local";

    internal static string EnsureSelfDevice(SqliteConnection connection)
    {
        // Begin an immediate (not deferred) transaction so the "does a self device
        // already exist" check and the insert that follows it are atomic against a
        // concurrent writer. A deferred transaction only takes SQLite's write lock at
        // its first write statement, so the SELECT below would still be unserialised:
        // two connections could both read "no self row" on a fresh database before
        // either commits, and both would mint and insert a distinct self device.
        // Do not "simplify" this back to a deferred/default transaction.
        using var transaction = connection.BeginTransaction(deferred: false);

        string? existing = ReadSelfDeviceId(connection, transaction);
        if (existing is not null)
        {
            transaction.Commit();
            return existing;
        }

        string deviceId = Guid.NewGuid().ToString();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Devices (DeviceId, DisplayName, Os, IsSelf, UpdatedAt)
                VALUES ($id, $name, 'win', 1, $now);
                """;
            insert.Parameters.AddWithValue("$id", deviceId);
            insert.Parameters.AddWithValue("$name", Environment.MachineName);
            insert.Parameters.AddWithValue("$now", Timestamps.UtcNowText());
            insert.ExecuteNonQuery();
        }

        foreach (string table in (string[])["ActivitySegment", "OpenAppSegment", "DailyInput"])
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE {table} SET DeviceId = $id WHERE DeviceId = $placeholder;";
            update.Parameters.AddWithValue("$id", deviceId);
            update.Parameters.AddWithValue("$placeholder", Placeholder);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return deviceId;
    }

    private static string? ReadSelfDeviceId(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DeviceId FROM Devices WHERE IsSelf = 1 LIMIT 1;";
        return command.ExecuteScalar() as string;
    }
}
