using Microsoft.Data.Sqlite;

namespace Daylane.Services;

/// <summary>
/// Append-only schema migrations. Index + 1 == schema version, tracked in PRAGMA user_version.
/// Never edit a shipped script; add a new one.
/// </summary>
internal static class Migrations
{
    internal static readonly string[] Scripts = [V1];

    internal static int CurrentVersion => Scripts.Length;

    internal static void Apply(SqliteConnection connection, string? databasePath = null)
        => Apply(connection, Scripts, databasePath);

    internal static void Apply(SqliteConnection connection, string[] scripts, string? databasePath)
    {
        int version = ReadUserVersion(connection);
        if (version >= scripts.Length)
        {
            return;
        }

        for (int i = version; i < scripts.Length; i++)
        {
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = scripts[i];
                command.ExecuteNonQuery();
            }

            // user_version lives in the database header and is transactional, so a
            // rollback leaves the version where it was.
            using (var setVersion = connection.CreateCommand())
            {
                setVersion.Transaction = transaction;
                setVersion.CommandText = $"PRAGMA user_version = {i + 1};";
                setVersion.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    internal static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private const string V1 = """
        CREATE TABLE IF NOT EXISTS DailyInput (
            LogDate TEXT PRIMARY KEY,
            KeyCount INTEGER NOT NULL DEFAULT 0,
            MouseClickCount INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS ActivitySegment (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            StartUtc TEXT NOT NULL,
            EndUtc TEXT NULL,
            ProcessName TEXT NOT NULL,
            ExePath TEXT NOT NULL,
            DisplayName TEXT NOT NULL,
            IsIdle INTEGER NOT NULL DEFAULT 0,
            KeyCount INTEGER NOT NULL DEFAULT 0,
            MouseClickCount INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_ActivitySegment_StartEnd
            ON ActivitySegment (StartUtc, EndUtc);

        CREATE INDEX IF NOT EXISTS IX_ActivitySegment_ExePath_Start
            ON ActivitySegment (ExePath, StartUtc);

        CREATE TABLE IF NOT EXISTS OpenAppSegment (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            StartUtc TEXT NOT NULL,
            EndUtc TEXT NULL,
            ProcessName TEXT NOT NULL,
            ExePath TEXT NOT NULL,
            DisplayName TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_OpenAppSegment_StartEnd
            ON OpenAppSegment (StartUtc, EndUtc);

        CREATE INDEX IF NOT EXISTS IX_OpenAppSegment_ExePath_Start
            ON OpenAppSegment (ExePath, StartUtc);
        """;
}
