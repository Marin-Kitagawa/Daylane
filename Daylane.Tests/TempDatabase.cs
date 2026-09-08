using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

/// <summary>Throwaway on-disk SQLite database. On-disk, not in-memory: the migration
/// framework backs the file up before upgrading, which an in-memory database cannot exercise.</summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _directory;

    public TempDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "daylane-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory, "daylane.db");
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5
        }.ConnectionString;
    }

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked handle should fail the test that leaked it, not every later test.
        }
    }
}
