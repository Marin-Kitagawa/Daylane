using System.Diagnostics;
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
        Persisted = Current;
    }

    public DaylaneSettings Current { get; private set; }

    /// <summary>The most recent settings known to have reached the database. Identical to
    /// <see cref="Current"/> unless a write failed, in which case Current is ahead of it by a
    /// change that will not survive a restart. Anything irreversible -- the retroactive
    /// ignore-rule recompute -- must read this rather than Current, including when it re-reads
    /// at execution time to let the newest rule set win.</summary>
    public DaylaneSettings Persisted { get; private set; }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>Applies a settings change and reports whether it reached the database.
    ///
    /// A failed write still applies in memory -- settings are not worth losing a session over,
    /// and silently ignoring "turn titles off" or "pause tracking" because a backup tool holds
    /// the file would be worse than not persisting it. But the failure must not be invisible:
    /// callers get it as the return value, and subscribers get it on the event, because an
    /// irreversible action taken on a change that vanishes at restart is the one thing that
    /// cannot be undone afterwards (an ignore-rule recompute against a rule that was never
    /// stored leaves history excluded with no rule left to delete).</summary>
    public bool Update(Func<DaylaneSettings, DaylaneSettings> mutate)
    {
        DaylaneSettings updated;
        bool persisted;
        lock (_writeLock)
        {
            updated = mutate(Current).Normalize();
            persisted = Write(updated);
            Current = updated;
            if (persisted)
            {
                Persisted = updated;
            }
        }

        Changed?.Invoke(this, new SettingsChangedEventArgs(updated, persisted));
        return persisted;
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

    /// <summary>Returns false if the write did not reach the database.</summary>
    private bool Write(DaylaneSettings settings)
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
            return true;
        }
        catch (SqliteException ex)
        {
            // Keep the in-memory value. Settings are not worth losing a session over -- but
            // report the failure rather than swallowing it: this change will not be there
            // after a restart, so nothing irreversible may be done on the strength of it.
            Debug.WriteLine($"Settings write failed: {ex.Message}");
            return false;
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

/// <summary>A settings change, plus whether it actually reached the database.
///
/// The flag exists for one reason: a change that did not persist is gone at the next restart,
/// so nothing irreversible may act on it. The retroactive ignore-rule recompute is the case in
/// point -- recomputing the whole table against a rule that was never stored would leave
/// history excluded with no rule left to delete, which is exactly the irreversibility the
/// ignore-rule design forbids. Everything that merely reflects the new value (the tray
/// checkbox, the Settings tab's own bindings) can and should ignore it.</summary>
internal sealed class SettingsChangedEventArgs : EventArgs
{
    internal SettingsChangedEventArgs(DaylaneSettings settings, bool persisted)
    {
        Settings = settings;
        Persisted = persisted;
    }

    internal DaylaneSettings Settings { get; }

    internal bool Persisted { get; }
}
