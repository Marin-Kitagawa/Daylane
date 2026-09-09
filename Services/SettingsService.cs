using System.Text.Json;
using System.Text.Json.Nodes;
using Daylane.Models;
using Microsoft.Data.Sqlite;

namespace Daylane.Services;

internal sealed class SettingsService
{
    private readonly string _connectionString;
    private readonly object _writeLock = new();

    public SettingsService(string connectionString)
    {
        _connectionString = connectionString;
        Current = Read().Normalize();
    }

    public DaylaneSettings Current { get; private set; }

    public event EventHandler<DaylaneSettings>? Changed;

    public void Update(Func<DaylaneSettings, DaylaneSettings> mutate)
    {
        DaylaneSettings updated;
        lock (_writeLock)
        {
            updated = mutate(Current).Normalize();
            Write(updated);
            Current = updated;
        }

        Changed?.Invoke(this, updated);
    }

    private DaylaneSettings Read()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Data FROM SettingsStore WHERE Id = 1;";
            if (command.ExecuteScalar() is string json && json.Length > 0)
            {
                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.DaylaneSettings)
                    ?? new DaylaneSettings();
            }
        }
        catch (SqliteException)
        {
        }
        catch (JsonException)
        {
            // A corrupt blob must not stop the app from starting; defaults are safe.
        }

        return new DaylaneSettings();
    }

    private void Write(DaylaneSettings settings)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            // Merge over whatever is stored rather than replacing it, so keys written by a
            // newer build survive being run by an older one.
            JsonObject merged = ReadRaw(connection) ?? new JsonObject();
            var patch = JsonSerializer.SerializeToNode(
                settings, SettingsJsonContext.Default.DaylaneSettings)!.AsObject();
            foreach (var property in patch)
            {
                merged[property.Key] = property.Value?.DeepClone();
            }

            using var command = connection.CreateCommand();
            // Upsert, not UPDATE: an UPDATE against a missing row 1 affects nothing and reports
            // success, so settings would stop persisting without a single error anywhere.
            command.CommandText = """
                INSERT INTO SettingsStore (Id, Data) VALUES (1, $data)
                ON CONFLICT(Id) DO UPDATE SET Data = $data;
                """;
            command.Parameters.AddWithValue("$data", merged.ToJsonString());
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Keep the in-memory value. Settings are not worth losing a session over.
        }
    }

    private static JsonObject? ReadRaw(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Data FROM SettingsStore WHERE Id = 1;";
        if (command.ExecuteScalar() is not string json || json.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json)?.AsObject();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
