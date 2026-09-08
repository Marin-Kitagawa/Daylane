# Daylane Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the migration framework, schema v2, settings store, themeable dark mode and first test project that every later Hindsight-parity sub-project depends on.

**Architecture:** An append-only migration script list replaces Daylane's single `if (version < 1)` DDL block, carrying the database to v2 with the columns and tables that titles, taxonomy, screen memory and sync will need. Settings move from `config.ini` into a single-row JSON table read through a live-updating service. Every color moves into theme-scoped resource dictionaries so a variant switch re-resolves the whole UI, including the custom-drawn timeline control and the native titlebar.

**Tech Stack:** C# 13 / .NET 10 (`net10.0-windows`), Avalonia 12.1.0, Microsoft.Data.Sqlite 10.0.10, xUnit, Win32 P/Invoke (`dwmapi`, `advapi32` via `Microsoft.Win32.Registry`).

**Spec:** [docs/superpowers/specs/2026-09-08-daylane-foundation-design.md](../specs/2026-09-08-daylane-foundation-design.md)

## Global Constraints

- **No AI features, ever.** No LLM engine, embeddings, model downloads, AI reports or chat. Permanent product constraint, not a deferral.
- **Windows only.** `net10.0-windows`; no macOS code paths.
- **Every new capture or sync capability ships disabled by default.** This sub-project adds storage only — nothing new is written to it here.
- Schema scripts are **append-only**. Never edit a shipped script; add a new one.
- Column naming is **PascalCase** (Daylane's existing convention), not Hindsight's snake_case.
- Timestamps are stored UTC via `DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O")` (`Services/DailyStatsStore.cs:646`). Do not introduce local-offset storage.
- Tombstone/LWW default for shared entities is the literal string `'1970-01-01T00:00:00Z'`.
- `IdleThresholdMinutes` bounds are 1–240 (`IdleMonitor.MinThresholdMinutes` / `MaxThresholdMinutes`).
- Commits are GPG-signed (`git commit -S`) with **no** `Co-Authored-By` trailer. Message style is plain imperative, matching the repo's history ("Bump version", "Smooth timeline wheel zoom").

---

### Task 1: Test project and database-path seam

Daylane has no tests and no `.sln`, and `DailyStatsStore` resolves its path from a static method with no seam (`Services/DailyStatsStore.cs:477`). Nothing else can be tested until this exists.

**Files:**
- Create: `Daylane.sln`
- Create: `Daylane.Tests/Daylane.Tests.csproj`
- Create: `Daylane.Tests/TempDatabase.cs`
- Create: `Daylane.Tests/DailyStatsStoreTests.cs`
- Modify: `Daylane.csproj` (add `InternalsVisibleTo`)
- Modify: `Services/DailyStatsStore.cs:20-31,477` (optional path parameter, expose connection string)

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `DailyStatsStore(string? databasePath = null)` — null keeps today's behavior (`AppContext.BaseDirectory/daylane.db`).
  - `internal string DailyStatsStore.ConnectionString { get; }`
  - `internal sealed class TempDatabase : IDisposable` with `string Path`, `string ConnectionString`, `SqliteConnection Open()`.

- [ ] **Step 1: Create the solution and test project**

```bash
dotnet new sln --name Daylane
dotnet sln add Daylane.csproj
```

Create `Daylane.Tests/Daylane.Tests.csproj`. It declares a plain `net10.0-windows` target and must **not** inherit the app's Release `RuntimeIdentifier`/`PublishSingleFile` properties:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Daylane.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Daylane.csproj" />
  </ItemGroup>

</Project>
```

```bash
dotnet sln add Daylane.Tests/Daylane.Tests.csproj
```

- [ ] **Step 2: Grant the test project access to internals**

Daylane is `internal` throughout. Add to `Daylane.csproj`, inside the existing `ItemGroup` that holds `AvaloniaResource`:

```xml
<InternalsVisibleTo Include="Daylane.Tests" />
```

- [ ] **Step 3: Write the temp-database helper**

WAL mode leaves `-wal` and `-shm` sidecar files, and Windows will not delete a file whose connection is still pooled — hence `ClearAllPools()` before cleanup.

Create `Daylane.Tests/TempDatabase.cs`:

```csharp
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
```

- [ ] **Step 4: Write the failing test**

Create `Daylane.Tests/DailyStatsStoreTests.cs`:

```csharp
using Daylane.Services;

namespace Daylane.Tests;

public class DailyStatsStoreTests
{
    [Fact]
    public void Constructor_CreatesDatabaseAtSuppliedPath()
    {
        using var temp = new TempDatabase();

        using var store = new DailyStatsStore(temp.DatabasePath);

        Assert.Equal(temp.DatabasePath, store.DatabasePath);
        Assert.True(File.Exists(temp.DatabasePath));
    }
}
```

- [ ] **Step 5: Run the test to verify it fails**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: compile error — `DailyStatsStore` has no constructor taking one argument.

- [ ] **Step 6: Add the path seam**

In `Services/DailyStatsStore.cs`, change the constructor at `:20` and the resolver at `:477`:

```csharp
    public DailyStatsStore(string? databasePath = null)
    {
        DatabasePath = databasePath ?? ResolveDatabasePath();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5
        }.ConnectionString;

        InitializeDatabase();
        _flushTimer = new Timer(_ => Flush(), null, 5000, 5000);
    }

    public string DatabasePath { get; }

    internal string ConnectionString => _connectionString;
```

`ResolveDatabasePath()` is unchanged, so `new DailyStatsStore()` — the only production call site, `Services/TrackingService.cs:32` — behaves exactly as before.

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 1 test.

- [ ] **Step 8: Verify the app still builds**

Run: `dotnet build Daylane.csproj`
Expected: no errors.

- [ ] **Step 9: Commit**

```bash
git add Daylane.sln Daylane.csproj Daylane.Tests/ Services/DailyStatsStore.cs
git commit -S -m "Add test project and a database path seam

Daylane had no tests. DailyStatsStore resolved its path from a static
method with no injection point, so migrations could not be exercised
against a throwaway file."
```

---

### Task 2: Migration framework

Replace the ad-hoc version check with an append-only script list. This task is a behavior-preserving refactor: the only script is V1, the existing DDL verbatim.

**Files:**
- Create: `Services/Migrations.cs`
- Create: `Daylane.Tests/MigrationsTests.cs`
- Modify: `Services/DailyStatsStore.cs:11,551-624` (delete `SchemaVersion` and the inline DDL, delegate to `Migrations.Apply`)

**Interfaces:**
- Consumes: `TempDatabase` (Task 1).
- Produces:
  - `internal static class Migrations` with:
    - `internal static readonly string[] Scripts`
    - `internal static int CurrentVersion { get; }` — equals `Scripts.Length`
    - `internal static void Apply(SqliteConnection connection, string? databasePath = null)`
    - `internal static int ReadUserVersion(SqliteConnection connection)`

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/MigrationsTests.cs`:

```csharp
using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class MigrationsTests
{
    private static HashSet<string> TableNames(SqliteConnection connection)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    [Fact]
    public void Apply_OnFreshDatabase_CreatesV1Tables()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        var tables = TableNames(connection);
        Assert.Contains("DailyInput", tables);
        Assert.Contains("ActivitySegment", tables);
        Assert.Contains("OpenAppSegment", tables);
    }

    [Fact]
    public void Apply_SetsUserVersionToCurrent()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(Migrations.CurrentVersion, Migrations.ReadUserVersion(connection));
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);
        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(Migrations.CurrentVersion, Migrations.ReadUserVersion(connection));
    }

    [Fact]
    public void Apply_PreservesExistingRows()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO DailyInput (LogDate, KeyCount, MouseClickCount) VALUES ('2026-09-01', 42, 7);";
            insert.ExecuteNonQuery();
        }

        Migrations.Apply(connection, temp.DatabasePath);

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT KeyCount, MouseClickCount FROM DailyInput WHERE LogDate = '2026-09-01';";
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(42L, reader.GetInt64(0));
        Assert.Equal(7L, reader.GetInt64(1));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: compile error — `Migrations` does not exist.

- [ ] **Step 3: Write the migration framework**

Create `Services/Migrations.cs`. `V1` is the DDL currently inlined at `DailyStatsStore.cs:576-612`, moved verbatim so databases already at version 1 are untouched.

```csharp
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
```

- [ ] **Step 4: Delegate from `DailyStatsStore`**

In `Services/DailyStatsStore.cs`, delete the `SchemaVersion` constant at `:11` and replace the body of `InitializeDatabase` after the pragma block (currently `:564-624`) with a single call. The method becomes:

```csharp
    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                """;
            pragma.ExecuteNonQuery();
        }

        Migrations.Apply(connection, DatabasePath);
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 5 tests.

- [ ] **Step 6: Verify against a real database**

The migration must be a no-op on a database from the shipped release. If `daylane.db` exists beside a built Daylane, copy it aside and confirm `PRAGMA user_version` reads 1 and no script runs:

```bash
dotnet build Daylane.csproj && dotnet test Daylane.Tests/Daylane.Tests.csproj
```

- [ ] **Step 7: Commit**

```bash
git add Services/Migrations.cs Services/DailyStatsStore.cs Daylane.Tests/MigrationsTests.cs
git commit -S -m "Extract schema into an append-only migration framework

Behavior-preserving: V1 is the existing DDL verbatim, so databases
already at user_version 1 skip it. Replaces the single if (version < 1)
block, which had no room for a second migration."
```

---

### Task 3: Migration backup and failure handling

A portable app's database is the user's only copy of their history. A failed upgrade must roll back, keep a backup, and refuse to run rather than operate on a schema it does not understand.

**Files:**
- Modify: `Services/Migrations.cs`
- Modify: `Daylane.Tests/MigrationsTests.cs`

**Interfaces:**
- Consumes: `Migrations.Apply` (Task 2).
- Produces: `Apply` throws `InvalidOperationException` on script failure. `App.axaml.cs:35-46` already catches `InvalidOperationException` around `new TrackingService()` and shows `CreateErrorWindow(ex.Message)`, so the failure surfaces as a dialog with no new UI code.

- [ ] **Step 1: Write the failing tests**

Append to `Daylane.Tests/MigrationsTests.cs`:

```csharp
    [Fact]
    public void Apply_WhenScriptFails_RollsBackAndKeepsVersion()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);
        int before = Migrations.ReadUserVersion(connection);

        string[] scripts =
        [
            .. Migrations.Scripts,
            "CREATE TABLE Good (Id INTEGER); SELECT this_is_not_valid_sql();"
        ];

        Assert.Throws<InvalidOperationException>(
            () => Migrations.Apply(connection, scripts, temp.DatabasePath));

        Assert.Equal(before, Migrations.ReadUserVersion(connection));
        Assert.DoesNotContain("Good", TableNames(connection));
    }

    [Fact]
    public void Apply_WhenUpgradingExistingDatabase_WritesBackup()
    {
        using var temp = new TempDatabase();
        using (var connection = temp.Open())
        {
            Migrations.Apply(connection, temp.DatabasePath);
        }

        SqliteConnection.ClearAllPools();
        string[] scripts = [.. Migrations.Scripts, "CREATE TABLE Later (Id INTEGER);"];

        using (var connection = temp.Open())
        {
            Migrations.Apply(connection, scripts, temp.DatabasePath);
        }

        Assert.True(File.Exists($"{temp.DatabasePath}.bak.v{Migrations.CurrentVersion}"));
    }

    [Fact]
    public void Apply_OnFreshDatabase_WritesNoBackup()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(temp.DatabasePath)!, "*.bak.*"));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter MigrationsTests`
Expected: `Apply_WhenScriptFails_RollsBackAndKeepsVersion` fails with `SqliteException` rather than `InvalidOperationException`; the backup tests fail because no `.bak` is written.

- [ ] **Step 3: Add backup and failure wrapping**

In `Services/Migrations.cs`, replace the `Apply(connection, scripts, databasePath)` body:

```csharp
    internal static void Apply(SqliteConnection connection, string[] scripts, string? databasePath)
    {
        int version = ReadUserVersion(connection);
        if (version >= scripts.Length)
        {
            return;
        }

        // Only back up when upgrading data that already exists. A fresh database has
        // nothing to lose, and writing a .bak of an empty file just litters the folder.
        if (version > 0 && databasePath is not null && File.Exists(databasePath))
        {
            TryBackup(databasePath, version);
        }

        for (int i = version; i < scripts.Length; i++)
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = scripts[i];
                    command.ExecuteNonQuery();
                }

                using (var setVersion = connection.CreateCommand())
                {
                    setVersion.Transaction = transaction;
                    setVersion.CommandText = $"PRAGMA user_version = {i + 1};";
                    setVersion.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (SqliteException ex)
            {
                transaction.Rollback();

                string backupNote = databasePath is null
                    ? string.Empty
                    : $" A backup of the previous database was kept at \"{databasePath}.bak.v{version}\".";

                throw new InvalidOperationException(
                    $"Daylane could not upgrade its database to version {i + 1}: {ex.Message}"
                    + backupNote
                    + " The database was left unchanged.",
                    ex);
            }
        }
    }

    private static void TryBackup(string databasePath, int fromVersion)
    {
        try
        {
            File.Copy(databasePath, $"{databasePath}.bak.v{fromVersion}", overwrite: true);
        }
        catch (IOException)
        {
            // A backup we cannot write must not block an upgrade the user needs.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/Migrations.cs Daylane.Tests/MigrationsTests.cs
git commit -S -m "Back up the database before upgrading and roll back failures

Daylane is portable, so daylane.db beside the exe is the user's only
copy. A failed script now rolls back with user_version untouched and
throws InvalidOperationException, which App already surfaces as an
error window instead of starting against a half-known schema."
```

---

### Task 4: Schema v2 — activity columns, backfill, dedup trigger

**Files:**
- Modify: `Services/Migrations.cs` (add `V2`, register in `Scripts`)
- Create: `Daylane.Tests/SchemaV2Tests.cs`

**Interfaces:**
- Consumes: `Migrations.Apply`, `Migrations.ReadUserVersion` (Tasks 2–3).
- Produces: `ActivitySegment` and `OpenAppSegment` each gain `LocalDate`, `LocalHour`, `DeviceId`, `RemoteId`, `UpdatedAt`, `Origin`; `ActivitySegment` additionally gains `WindowTitle`, `UrlHost`, `Excluded`. `Migrations.CurrentVersion` becomes 2.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/SchemaV2Tests.cs`:

```csharp
using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class SchemaV2Tests
{
    private const string Epoch = "1970-01-01T00:00:00Z";

    private static HashSet<string> ColumnNames(SqliteConnection connection, string table)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static void InsertV1Segment(
        SqliteConnection connection, string startUtc, string processName, int isIdle)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ActivitySegment
                (StartUtc, EndUtc, ProcessName, ExePath, DisplayName, IsIdle, KeyCount, MouseClickCount)
            VALUES ($start, $end, $process, $path, $display, $idle, 0, 0);
            """;
        command.Parameters.AddWithValue("$start", startUtc);
        command.Parameters.AddWithValue("$end", startUtc);
        command.Parameters.AddWithValue("$process", processName);
        command.Parameters.AddWithValue("$path", $@"C:\Apps\{processName}.exe");
        command.Parameters.AddWithValue("$display", processName);
        command.Parameters.AddWithValue("$idle", isIdle);
        command.ExecuteNonQuery();
    }

    /// <summary>Applies V1 only, so a test can seed rows the way the shipped release would
    /// have, then upgrade them.</summary>
    private static void ApplyV1Only(SqliteConnection connection, string path)
        => Migrations.Apply(connection, [Migrations.Scripts[0]], path);

    [Fact]
    public void V2_AddsActivityColumns()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        var columns = ColumnNames(connection, "ActivitySegment");
        Assert.Contains("WindowTitle", columns);
        Assert.Contains("UrlHost", columns);
        Assert.Contains("LocalDate", columns);
        Assert.Contains("LocalHour", columns);
        Assert.Contains("DeviceId", columns);
        Assert.Contains("RemoteId", columns);
        Assert.Contains("UpdatedAt", columns);
        Assert.Contains("Origin", columns);
        Assert.Contains("Excluded", columns);
    }

    [Fact]
    public void V2_AddsOpenAppColumns()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        var columns = ColumnNames(connection, "OpenAppSegment");
        Assert.Contains("LocalDate", columns);
        Assert.Contains("LocalHour", columns);
        Assert.Contains("DeviceId", columns);
        Assert.Contains("RemoteId", columns);
        Assert.Contains("UpdatedAt", columns);
        Assert.Contains("Origin", columns);
    }

    [Fact]
    public void V2_BackfillsLocalDateAndHourFromLocalTimeZone()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        ApplyV1Only(connection, temp.DatabasePath);

        var startUtc = new DateTime(2026, 3, 15, 22, 40, 0, DateTimeKind.Utc);
        InsertV1Segment(connection, startUtc.ToString("O"), "code", isIdle: 0);

        Migrations.Apply(connection, temp.DatabasePath);

        DateTime expectedLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc, TimeZoneInfo.Local);
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT LocalDate, LocalHour FROM ActivitySegment;";
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expectedLocal.ToString("yyyy-MM-dd"), reader.GetString(0));
        Assert.Equal(expectedLocal.Hour, reader.GetInt32(1));
    }

    [Fact]
    public void V2_BackfillsRemoteIdForPreExistingRows()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        ApplyV1Only(connection, temp.DatabasePath);
        InsertV1Segment(connection, "2026-09-01T10:00:00.0000000Z", "code", isIdle: 0);

        Migrations.Apply(connection, temp.DatabasePath);

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT Id, RemoteId FROM ActivitySegment;";
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(reader.GetInt64(0), reader.GetInt64(1));
    }

    [Fact]
    public void V2_TriggerAssignsRemoteIdOnLocalInsert()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);

        InsertV1Segment(connection, "2026-09-02T08:00:00.0000000Z", "chrome", isIdle: 0);

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT Id, RemoteId FROM ActivitySegment;";
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(reader.GetInt64(0), reader.GetInt64(1));
    }

    [Fact]
    public void V2_BackfillsUpdatedAtFromEndUtc()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        ApplyV1Only(connection, temp.DatabasePath);
        InsertV1Segment(connection, "2026-09-01T10:00:00.0000000Z", "code", isIdle: 0);

        Migrations.Apply(connection, temp.DatabasePath);

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT UpdatedAt FROM ActivitySegment;";
        Assert.NotEqual(Epoch, (string)read.ExecuteScalar()!);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter SchemaV2Tests`
Expected: all six fail — `Migrations.Scripts` has one entry, so none of the columns exist.

- [ ] **Step 3: Add the V2 script**

In `Services/Migrations.cs`, change the list and add the constant:

```csharp
    internal static readonly string[] Scripts = [V1, V2];
```

```csharp
    private const string V2 = """
        ALTER TABLE ActivitySegment ADD COLUMN WindowTitle TEXT NULL;
        ALTER TABLE ActivitySegment ADD COLUMN UrlHost     TEXT NULL;
        ALTER TABLE ActivitySegment ADD COLUMN LocalDate   TEXT NULL;
        ALTER TABLE ActivitySegment ADD COLUMN LocalHour   INTEGER NULL;
        ALTER TABLE ActivitySegment ADD COLUMN DeviceId    TEXT NOT NULL DEFAULT 'local';
        ALTER TABLE ActivitySegment ADD COLUMN RemoteId    TEXT NULL;
        ALTER TABLE ActivitySegment ADD COLUMN UpdatedAt   TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z';
        ALTER TABLE ActivitySegment ADD COLUMN Origin      TEXT NOT NULL DEFAULT 'local';
        ALTER TABLE ActivitySegment ADD COLUMN Excluded    INTEGER NOT NULL DEFAULT 0;

        ALTER TABLE OpenAppSegment ADD COLUMN LocalDate TEXT NULL;
        ALTER TABLE OpenAppSegment ADD COLUMN LocalHour INTEGER NULL;
        ALTER TABLE OpenAppSegment ADD COLUMN DeviceId  TEXT NOT NULL DEFAULT 'local';
        ALTER TABLE OpenAppSegment ADD COLUMN RemoteId  TEXT NULL;
        ALTER TABLE OpenAppSegment ADD COLUMN UpdatedAt TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z';
        ALTER TABLE OpenAppSegment ADD COLUMN Origin    TEXT NOT NULL DEFAULT 'local';

        -- 'localtime' resolves each timestamp with the OS rules in force at that instant,
        -- so spans either side of a DST change land on the right calendar day.
        UPDATE ActivitySegment
           SET LocalDate = date(StartUtc, 'localtime'),
               LocalHour = CAST(strftime('%H', StartUtc, 'localtime') AS INTEGER),
               UpdatedAt = COALESCE(EndUtc, StartUtc)
         WHERE LocalDate IS NULL;

        UPDATE OpenAppSegment
           SET LocalDate = date(StartUtc, 'localtime'),
               LocalHour = CAST(strftime('%H', StartUtc, 'localtime') AS INTEGER),
               UpdatedAt = COALESCE(EndUtc, StartUtc)
         WHERE LocalDate IS NULL;

        -- The triggers below are AFTER INSERT only, so rows that already existed would
        -- keep RemoteId NULL forever and be re-pulled as duplicates on the first sync.
        UPDATE ActivitySegment SET RemoteId = Id WHERE RemoteId IS NULL;
        UPDATE OpenAppSegment   SET RemoteId = Id WHERE RemoteId IS NULL;

        CREATE INDEX IF NOT EXISTS IX_ActivitySegment_LocalDate
            ON ActivitySegment (LocalDate);
        CREATE INDEX IF NOT EXISTS IX_ActivitySegment_LocalDateHour
            ON ActivitySegment (LocalDate, LocalHour);
        CREATE UNIQUE INDEX IF NOT EXISTS UX_ActivitySegment_Device_Remote
            ON ActivitySegment (DeviceId, RemoteId);

        CREATE INDEX IF NOT EXISTS IX_OpenAppSegment_LocalDate
            ON OpenAppSegment (LocalDate);
        CREATE UNIQUE INDEX IF NOT EXISTS UX_OpenAppSegment_Device_Remote
            ON OpenAppSegment (DeviceId, RemoteId);

        CREATE TRIGGER IF NOT EXISTS TR_ActivitySegment_RemoteId
        AFTER INSERT ON ActivitySegment WHEN NEW.RemoteId IS NULL
        BEGIN
            UPDATE ActivitySegment SET RemoteId = NEW.Id WHERE Id = NEW.Id;
        END;

        CREATE TRIGGER IF NOT EXISTS TR_OpenAppSegment_RemoteId
        AFTER INSERT ON OpenAppSegment WHEN NEW.RemoteId IS NULL
        BEGIN
            UPDATE OpenAppSegment SET RemoteId = NEW.Id WHERE Id = NEW.Id;
        END;
        """;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 14 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/Migrations.cs Daylane.Tests/SchemaV2Tests.cs
git commit -S -m "Add schema v2 activity columns, local-date backfill and dedup keys

Window title and browser host for the app-detail work, LocalDate and
LocalHour so report windows filter without converting every row, and
DeviceId/RemoteId/UpdatedAt/Origin so cloud sync is not a later table
rewrite. Pre-existing rows get RemoteId backfilled explicitly: the
trigger fires on insert only, and rows without it would re-pull as
duplicates on the first sync."
```

---

### Task 5: Schema v2 — `DailyInput` composite key

`DailyInput`'s primary key is `LogDate` alone, so two devices' counts collide. SQLite cannot alter a primary key, so the table is rebuilt.

**Files:**
- Modify: `Services/Migrations.cs` (extend `V2`)
- Modify: `Daylane.Tests/SchemaV2Tests.cs`
- Modify: `Services/DailyStatsStore.cs` (`GetTotalsForDate` at `:38-59`, and the flush upsert)

**Interfaces:**
- Consumes: `V2` (Task 4).
- Produces: `DailyInput (LogDate, DeviceId, KeyCount, MouseClickCount, UpdatedAt)` with `PRIMARY KEY (LogDate, DeviceId)`.

- [ ] **Step 1: Write the failing tests**

Append to `Daylane.Tests/SchemaV2Tests.cs`:

```csharp
    [Fact]
    public void V2_RebuildsDailyInputWithCompositeKey()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        var columns = ColumnNames(connection, "DailyInput");
        Assert.Contains("DeviceId", columns);
        Assert.Contains("UpdatedAt", columns);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('DailyInput') WHERE pk > 0;";
        Assert.Equal(2L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void V2_PreservesDailyInputCounts()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        ApplyV1Only(connection, temp.DatabasePath);

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO DailyInput (LogDate, KeyCount, MouseClickCount) VALUES ('2026-08-30', 1234, 56);";
            insert.ExecuteNonQuery();
        }

        Migrations.Apply(connection, temp.DatabasePath);

        using var read = connection.CreateCommand();
        read.CommandText =
            "SELECT DeviceId, KeyCount, MouseClickCount FROM DailyInput WHERE LogDate = '2026-08-30';";
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("local", reader.GetString(0));
        Assert.Equal(1234L, reader.GetInt64(1));
        Assert.Equal(56L, reader.GetInt64(2));
    }

    [Fact]
    public void V2_AllowsSameDateOnTwoDevices()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO DailyInput (LogDate, DeviceId, KeyCount, MouseClickCount)
                VALUES ('2026-09-01', 'device-a', 10, 1),
                       ('2026-09-01', 'device-b', 20, 2);
            """;
        insert.ExecuteNonQuery();

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM DailyInput WHERE LogDate = '2026-09-01';";
        Assert.Equal(2L, (long)count.ExecuteScalar()!);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter SchemaV2Tests`
Expected: the three new tests fail — `DailyInput` has no `DeviceId`.

- [ ] **Step 3: Extend V2 with the table rebuild**

Append to the `V2` string, after the trigger definitions:

```sql
        CREATE TABLE DailyInput_v2 (
            LogDate         TEXT NOT NULL,
            DeviceId        TEXT NOT NULL DEFAULT 'local',
            KeyCount        INTEGER NOT NULL DEFAULT 0,
            MouseClickCount INTEGER NOT NULL DEFAULT 0,
            UpdatedAt       TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z',
            PRIMARY KEY (LogDate, DeviceId)
        );

        INSERT INTO DailyInput_v2 (LogDate, DeviceId, KeyCount, MouseClickCount)
            SELECT LogDate, 'local', KeyCount, MouseClickCount FROM DailyInput;

        DROP TABLE DailyInput;
        ALTER TABLE DailyInput_v2 RENAME TO DailyInput;
```

- [ ] **Step 4: Scope the store's reads and writes to this device**

`DailyInput` now has two key columns, so unqualified reads would sum across devices once sync lands. In `Services/DailyStatsStore.cs`, `GetTotalsForDate` (`:38-59`) becomes an explicit aggregate — correct both before and after a device id exists:

```csharp
        command.CommandText = """
            SELECT COALESCE(SUM(KeyCount), 0), COALESCE(SUM(MouseClickCount), 0)
            FROM DailyInput
            WHERE LogDate = $date;
            """;
```

Because the aggregate always returns one row, drop the `if (!reader.Read())` early return and read the first row directly.

Find the flush upsert that writes `DailyInput` (search `INSERT INTO DailyInput` in `TryWriteBatch`) and add the key column so the `ON CONFLICT` target matches the new primary key:

```csharp
            INSERT INTO DailyInput (LogDate, DeviceId, KeyCount, MouseClickCount, UpdatedAt)
            VALUES ($date, 'local', $keys, $clicks, $now)
            ON CONFLICT (LogDate, DeviceId) DO UPDATE SET
                KeyCount = KeyCount + $keys,
                MouseClickCount = MouseClickCount + $clicks,
                UpdatedAt = $now;
```

Bind `$now` to `DateTime.UtcNow.ToString("O")`. The literal `'local'` is rewritten to the real device id by Task 7.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 17 tests.

- [ ] **Step 6: Verify input counting still works end to end**

Run: `dotnet run --project Daylane.csproj`
Type and click for a few seconds, wait past the 5-second flush timer, and confirm the Day view's key and click counts increase.

- [ ] **Step 7: Commit**

```bash
git add Services/Migrations.cs Services/DailyStatsStore.cs Daylane.Tests/SchemaV2Tests.cs
git commit -S -m "Key DailyInput by date and device

LogDate alone collides the moment a second device syncs the same day.
SQLite cannot alter a primary key, so v2 copies the table through a
rebuild and the store's reads become an explicit SUM."
```

---

### Task 6: Schema v2 — taxonomy, sync and settings tables

**Files:**
- Modify: `Services/Migrations.cs` (extend `V2`)
- Create: `Daylane.Tests/SchemaV2TablesTests.cs`

**Interfaces:**
- Consumes: `V2` (Tasks 4–5).
- Produces: tables `SettingsStore`, `SuperCategories`, `Categories`, `AppGroups`, `AppGroupMembers`, `ProcessPaths`, `AppIcons`, `Devices`, `SyncOutbox`, `SyncCursor`, `AuthState`, seeded built-in categories, and a backfilled single-member group per process name.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/SchemaV2TablesTests.cs`:

```csharp
using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class SchemaV2TablesTests
{
    private static void InsertV1Segment(SqliteConnection connection, string processName, int isIdle)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ActivitySegment
                (StartUtc, EndUtc, ProcessName, ExePath, DisplayName, IsIdle, KeyCount, MouseClickCount)
            VALUES ('2026-09-01T10:00:00.0000000Z', '2026-09-01T10:05:00.0000000Z',
                    $process, $path, $display, $idle, 0, 0);
            """;
        command.Parameters.AddWithValue("$process", processName);
        command.Parameters.AddWithValue("$path", $@"C:\Apps\{processName}.exe");
        command.Parameters.AddWithValue("$display", processName);
        command.Parameters.AddWithValue("$idle", isIdle);
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Theory]
    [InlineData("SettingsStore")]
    [InlineData("SuperCategories")]
    [InlineData("Categories")]
    [InlineData("AppGroups")]
    [InlineData("AppGroupMembers")]
    [InlineData("ProcessPaths")]
    [InlineData("AppIcons")]
    [InlineData("Devices")]
    [InlineData("SyncOutbox")]
    [InlineData("SyncCursor")]
    [InlineData("AuthState")]
    public void V2_CreatesTable(string table)
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(
            1L,
            Scalar(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';"));
    }

    [Fact]
    public void V2_SeedsSettingsAndAuthSingletonRows()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM SettingsStore WHERE Id = 1;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM AuthState WHERE Id = 1;"));

        using var data = connection.CreateCommand();
        data.CommandText = "SELECT Data FROM SettingsStore WHERE Id = 1;";
        Assert.Equal("{}", (string)data.ExecuteScalar()!);
    }

    [Fact]
    public void V2_SeedsBuiltinCategories()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(5L, Scalar(connection, "SELECT COUNT(*) FROM Categories WHERE Builtin = 1;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM Categories WHERE Id = 'other';"));
    }

    [Fact]
    public void V2_BackfillsOneGroupPerProcessNameWithProcessNameAsId()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, [Migrations.Scripts[0]], temp.DatabasePath);
        InsertV1Segment(connection, "code", isIdle: 0);
        InsertV1Segment(connection, "chrome", isIdle: 0);

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM AppGroups;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM AppGroups WHERE Id = 'code';"));
        Assert.Equal(
            2L,
            Scalar(connection, "SELECT COUNT(*) FROM AppGroupMembers WHERE ProcessName = GroupId;"));
    }

    [Fact]
    public void V2_ExcludesIdleFromAppGroups()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, [Migrations.Scripts[0]], temp.DatabasePath);
        InsertV1Segment(connection, "code", isIdle: 0);
        InsertV1Segment(connection, "Idle", isIdle: 1);

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM AppGroups WHERE Id = 'Idle';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM AppGroups;"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter SchemaV2TablesTests`
Expected: all fail — none of the tables exist.

- [ ] **Step 3: Extend V2 with the new tables**

Append to `V2`. `AppGroups` is populated before `AppGroupMembers` because the member row carries a foreign key to it (Microsoft.Data.Sqlite enables foreign-key enforcement by default).

```sql
        CREATE TABLE IF NOT EXISTS SettingsStore (
            Id   INTEGER PRIMARY KEY CHECK (Id = 1),
            Data TEXT NOT NULL
        );
        INSERT OR IGNORE INTO SettingsStore (Id, Data) VALUES (1, '{}');

        CREATE TABLE IF NOT EXISTS SuperCategories (
            Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Color TEXT NOT NULL,
            Icon TEXT NOT NULL DEFAULT 'Layers', SortOrder INTEGER NOT NULL DEFAULT 0,
            UpdatedAt TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z', DeletedAt TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS Categories (
            Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Color TEXT NOT NULL,
            Icon TEXT NOT NULL DEFAULT 'Tag', Builtin INTEGER NOT NULL DEFAULT 0,
            SortOrder INTEGER NOT NULL DEFAULT 0, SuperCategoryId TEXT NULL,
            UpdatedAt TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z', DeletedAt TEXT NULL
        );

        INSERT OR IGNORE INTO Categories (Id, Name, Color, Icon, Builtin, SortOrder) VALUES
            ('work',   'Work',          '#2F9E6B', 'Briefcase', 1, 0),
            ('code',   'Development',   '#6366F1', 'Code',      1, 1),
            ('browse', 'Browsing',      '#F59E0B', 'Globe',     1, 2),
            ('other',  'Uncategorized', '#9CA3AF', 'Tag',       1, 3),
            ('hidden', 'Hidden',        '#6B7280', 'EyeOff',    1, 4);

        CREATE TABLE IF NOT EXISTS AppGroups (
            Id TEXT PRIMARY KEY, DisplayName TEXT NOT NULL, CategoryId TEXT NULL,
            UpdatedAt TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z', DeletedAt TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS AppGroupMembers (
            ProcessName TEXT PRIMARY KEY,
            GroupId TEXT NOT NULL REFERENCES AppGroups(Id),
            UpdatedAt TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z', DeletedAt TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_AppGroupMembers_Group ON AppGroupMembers (GroupId);

        -- Group id equals the process name, so two devices independently arrive at the
        -- same id with no coordination. IsIdle = 0 keeps the synthetic 'Idle' process
        -- (Models/ForegroundApp.cs) out of the app list: away time is not an app.
        INSERT OR IGNORE INTO AppGroups (Id, DisplayName)
            SELECT ProcessName, MIN(DisplayName) FROM ActivitySegment
             WHERE IsIdle = 0 GROUP BY ProcessName
            UNION
            SELECT ProcessName, MIN(DisplayName) FROM OpenAppSegment
             GROUP BY ProcessName;

        INSERT OR IGNORE INTO AppGroupMembers (ProcessName, GroupId)
            SELECT Id, Id FROM AppGroups;

        CREATE TABLE IF NOT EXISTS ProcessPaths (
            ProcessName TEXT PRIMARY KEY, ExePath TEXT NOT NULL, SeenAt TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS AppIcons (
            ProcessName TEXT PRIMARY KEY, IconPng BLOB NOT NULL,
            UpdatedAt TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z', DeletedAt TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS Devices (
            DeviceId TEXT PRIMARY KEY, DisplayName TEXT NOT NULL,
            Color TEXT NOT NULL DEFAULT '#2F9E6B', Icon TEXT NOT NULL DEFAULT 'Monitor',
            Os TEXT NULL, LastSeenAt TEXT NULL, IsSelf INTEGER NOT NULL DEFAULT 0,
            UpdatedAt TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z', DeletedAt TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS SyncOutbox (
            Id INTEGER PRIMARY KEY AUTOINCREMENT, Op TEXT NOT NULL, Entity TEXT NOT NULL,
            EntityPk TEXT NOT NULL, Payload TEXT NOT NULL, CreatedAt TEXT NOT NULL,
            Attempts INTEGER NOT NULL DEFAULT 0, LastError TEXT NULL, NextRetryAt TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_SyncOutbox_Due ON SyncOutbox (NextRetryAt);

        CREATE TABLE IF NOT EXISTS SyncCursor (
            Entity TEXT PRIMARY KEY,
            LastPulledAt TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z'
        );

        CREATE TABLE IF NOT EXISTS AuthState (
            Id INTEGER PRIMARY KEY CHECK (Id = 1), Uid TEXT NULL, Email TEXT NULL,
            RefreshTokenEnc BLOB NULL, AccessToken TEXT NULL, ExpiresAt TEXT NULL
        );
        INSERT OR IGNORE INTO AuthState (Id) VALUES (1);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 32 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/Migrations.cs Daylane.Tests/SchemaV2TablesTests.cs
git commit -S -m "Add taxonomy, sync and settings tables to schema v2

Ports Hindsight's classification chain (AppGroupMembers -> AppGroups ->
Categories) and its last-write-wins plus tombstone plus outbox
infrastructure, without the three artifacts its own schema doc marks
dead: the deprecated per-activity category, the never-written image
hash, and the app_categories mirror.

Group ids are the process name so two devices converge without
coordination, and the synthetic Idle process is excluded."
```

---

### Task 7: Device identity

Migration SQL cannot mint a UUID, so `DeviceId` defaults to the literal `'local'` and a C# step reconciles it.

**Files:**
- Create: `Services/DeviceIdentity.cs`
- Create: `Daylane.Tests/DeviceIdentityTests.cs`
- Modify: `Services/DailyStatsStore.cs` (`InitializeDatabase`, and the `DailyInput` upsert from Task 5)

**Interfaces:**
- Consumes: `Devices` table (Task 6).
- Produces:
  - `internal static string DeviceIdentity.EnsureSelfDevice(SqliteConnection connection)` — returns the self device id.
  - `internal string DailyStatsStore.DeviceId { get; }`

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/DeviceIdentityTests.cs`:

```csharp
using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class DeviceIdentityTests
{
    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void EnsureSelfDevice_CreatesOneSelfRow()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);

        string deviceId = DeviceIdentity.EnsureSelfDevice(connection);

        Assert.True(Guid.TryParse(deviceId, out _));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM Devices WHERE IsSelf = 1;"));
    }

    [Fact]
    public void EnsureSelfDevice_IsIdempotent()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);

        string first = DeviceIdentity.EnsureSelfDevice(connection);
        string second = DeviceIdentity.EnsureSelfDevice(connection);

        Assert.Equal(first, second);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM Devices;"));
    }

    [Fact]
    public void EnsureSelfDevice_RewritesLocalPlaceholderRows()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);

        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO ActivitySegment
                    (StartUtc, ProcessName, ExePath, DisplayName, IsIdle, KeyCount, MouseClickCount)
                VALUES ('2026-09-01T10:00:00.0000000Z', 'code', 'C:\Apps\code.exe', 'code', 0, 0, 0);
                INSERT INTO OpenAppSegment (StartUtc, ProcessName, ExePath, DisplayName)
                VALUES ('2026-09-01T10:00:00.0000000Z', 'code', 'C:\Apps\code.exe', 'code');
                INSERT INTO DailyInput (LogDate, DeviceId, KeyCount, MouseClickCount)
                VALUES ('2026-09-01', 'local', 5, 1);
                """;
            seed.ExecuteNonQuery();
        }

        string deviceId = DeviceIdentity.EnsureSelfDevice(connection);

        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM ActivitySegment WHERE DeviceId = 'local';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM OpenAppSegment WHERE DeviceId = 'local';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM DailyInput WHERE DeviceId = 'local';"));
        Assert.Equal(
            1L,
            Scalar(connection, $"SELECT COUNT(*) FROM ActivitySegment WHERE DeviceId = '{deviceId}';"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter DeviceIdentityTests`
Expected: compile error — `DeviceIdentity` does not exist.

- [ ] **Step 3: Implement device identity**

Create `Services/DeviceIdentity.cs`:

```csharp
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
        string? existing = ReadSelfDeviceId(connection);
        if (existing is not null)
        {
            return existing;
        }

        string deviceId = Guid.NewGuid().ToString();
        using var transaction = connection.BeginTransaction();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Devices (DeviceId, DisplayName, Os, IsSelf, UpdatedAt)
                VALUES ($id, $name, 'win', 1, $now);
                """;
            insert.Parameters.AddWithValue("$id", deviceId);
            insert.Parameters.AddWithValue("$name", Environment.MachineName);
            insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
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

    private static string? ReadSelfDeviceId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DeviceId FROM Devices WHERE IsSelf = 1 LIMIT 1;";
        return command.ExecuteScalar() as string;
    }
}
```

- [ ] **Step 4: Resolve the device id at startup and use it when writing**

In `Services/DailyStatsStore.cs`, capture the id in `InitializeDatabase` and store it:

```csharp
    private string _deviceId = DeviceIdentity.Placeholder;

    internal string DeviceId => _deviceId;
```

```csharp
        Migrations.Apply(connection, DatabasePath);
        _deviceId = DeviceIdentity.EnsureSelfDevice(connection);
```

Then replace the literal `'local'` in the Task 5 `DailyInput` upsert with a bound parameter:

```csharp
            INSERT INTO DailyInput (LogDate, DeviceId, KeyCount, MouseClickCount, UpdatedAt)
            VALUES ($date, $device, $keys, $clicks, $now)
            ON CONFLICT (LogDate, DeviceId) DO UPDATE SET
```

`TryWriteBatch` is `static` and takes only a connection string, so pass the device id in as a parameter alongside it and bind `$device`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 35 tests.

- [ ] **Step 6: Commit**

```bash
git add Services/DeviceIdentity.cs Services/DailyStatsStore.cs Daylane.Tests/DeviceIdentityTests.cs
git commit -S -m "Mint a stable device id and promote placeholder rows

Schema v2 defaults DeviceId to 'local' because migration SQL cannot
generate a UUID. Resolve a real id on first run, record this machine in
Devices, and rewrite the placeholder across all three capture tables."
```

---

### Task 8: Settings model and store

**Files:**
- Create: `Models/DaylaneSettings.cs`
- Create: `Services/SettingsService.cs`
- Create: `Daylane.Tests/SettingsServiceTests.cs`

**Interfaces:**
- Consumes: `SettingsStore` table (Task 6), `DailyStatsStore.ConnectionString` (Task 1).
- Produces:
  - `internal sealed record DaylaneSettings` with `Appearance` (string), `IdleThresholdMinutes` (int), `TrackingEnabled` (bool), `AutoStart` (bool), `ShowWindowOnAutoStart` (bool), `MinimizeToTray` (bool), `RetentionDays` (int); plus `DaylaneSettings Normalize()`.
  - `internal sealed class SettingsService(string connectionString)` with `DaylaneSettings Current { get; }`, `event EventHandler<DaylaneSettings>? Changed`, `void Update(Func<DaylaneSettings, DaylaneSettings> mutate)`.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/SettingsServiceTests.cs`:

```csharp
using Daylane.Models;
using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class SettingsServiceTests
{
    private static TempDatabase MigratedDatabase()
    {
        var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);
        return temp;
    }

    [Fact]
    public void Current_OnEmptyStore_ReturnsDocumentedDefaults()
    {
        using var temp = MigratedDatabase();

        var service = new SettingsService(temp.ConnectionString);

        Assert.Equal("system", service.Current.Appearance);
        Assert.Equal(15, service.Current.IdleThresholdMinutes);
        Assert.True(service.Current.TrackingEnabled);
        Assert.False(service.Current.ShowWindowOnAutoStart);
        Assert.True(service.Current.MinimizeToTray);
        Assert.Equal(0, service.Current.RetentionDays);
    }

    [Fact]
    public void Update_PersistsAcrossInstances()
    {
        using var temp = MigratedDatabase();
        var service = new SettingsService(temp.ConnectionString);

        service.Update(s => s with { Appearance = "dark", RetentionDays = 90 });

        var reloaded = new SettingsService(temp.ConnectionString);
        Assert.Equal("dark", reloaded.Current.Appearance);
        Assert.Equal(90, reloaded.Current.RetentionDays);
    }

    [Fact]
    public void Update_RaisesChanged()
    {
        using var temp = MigratedDatabase();
        var service = new SettingsService(temp.ConnectionString);
        DaylaneSettings? observed = null;
        service.Changed += (_, s) => observed = s;

        service.Update(s => s with { Appearance = "light" });

        Assert.NotNull(observed);
        Assert.Equal("light", observed!.Appearance);
    }

    [Fact]
    public void Update_PreservesKeysItDoesNotKnow()
    {
        using var temp = MigratedDatabase();
        using (var connection = temp.Open())
        {
            using var seed = connection.CreateCommand();
            seed.CommandText =
                """UPDATE SettingsStore SET Data = '{"appearance":"dark","futureFlag":true}' WHERE Id = 1;""";
            seed.ExecuteNonQuery();
        }

        var service = new SettingsService(temp.ConnectionString);
        service.Update(s => s with { RetentionDays = 30 });

        using var read = temp.Open();
        using var command = read.CreateCommand();
        command.CommandText = "SELECT Data FROM SettingsStore WHERE Id = 1;";
        Assert.Contains("futureFlag", (string)command.ExecuteScalar()!);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(500, 240)]
    [InlineData(30, 30)]
    public void Normalize_ClampsIdleThreshold(int stored, int expected)
    {
        var settings = new DaylaneSettings { IdleThresholdMinutes = stored }.Normalize();

        Assert.Equal(expected, settings.IdleThresholdMinutes);
    }

    [Theory]
    [InlineData("dark", "dark")]
    [InlineData("light", "light")]
    [InlineData("system", "system")]
    [InlineData("neon", "system")]
    public void Normalize_RejectsUnknownAppearance(string stored, string expected)
    {
        var settings = new DaylaneSettings { Appearance = stored }.Normalize();

        Assert.Equal(expected, settings.Appearance);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter SettingsServiceTests`
Expected: compile error — `DaylaneSettings` and `SettingsService` do not exist.

- [ ] **Step 3: Write the settings model**

Create `Models/DaylaneSettings.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Daylane.Models;

internal sealed record DaylaneSettings
{
    public const string AppearanceSystem = "system";
    public const string AppearanceLight = "light";
    public const string AppearanceDark = "dark";

    [JsonPropertyName("appearance")]
    public string Appearance { get; init; } = AppearanceSystem;

    [JsonPropertyName("idleThresholdMinutes")]
    public int IdleThresholdMinutes { get; init; } = 15;

    [JsonPropertyName("trackingEnabled")]
    public bool TrackingEnabled { get; init; } = true;

    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; init; }

    [JsonPropertyName("showWindowOnAutoStart")]
    public bool ShowWindowOnAutoStart { get; init; }

    [JsonPropertyName("minimizeToTray")]
    public bool MinimizeToTray { get; init; } = true;

    /// <summary>0 keeps history forever, which is the behavior before this setting existed.</summary>
    [JsonPropertyName("retentionDays")]
    public int RetentionDays { get; init; }

    /// <summary>Set once the pre-settings-store config.ini has been imported. A dedicated flag
    /// rather than "is the threshold still the default", so a user who deliberately chose the
    /// default value is not silently overwritten by a stale config.ini.</summary>
    [JsonPropertyName("legacyConfigImported")]
    public bool LegacyConfigImported { get; init; }

    /// <summary>Clamps anything a hand-edited store or an older build could have written.</summary>
    public DaylaneSettings Normalize() => this with
    {
        Appearance = Appearance is AppearanceLight or AppearanceDark or AppearanceSystem
            ? Appearance
            : AppearanceSystem,
        IdleThresholdMinutes = Math.Clamp(IdleThresholdMinutes, 1, 240),
        RetentionDays = Math.Max(0, RetentionDays)
    };
}

[JsonSerializable(typeof(DaylaneSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
```

The bounds 1 and 240 mirror `IdleMonitor.MinThresholdMinutes` / `MaxThresholdMinutes`. A source-generated context is used instead of reflection so the single-file release build stays safe if trimming is ever enabled.

- [ ] **Step 4: Write the settings service**

Create `Services/SettingsService.cs`:

```csharp
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
            command.CommandText = "UPDATE SettingsStore SET Data = $data WHERE Id = 1;";
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
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 47 tests.

- [ ] **Step 6: Commit**

```bash
git add Models/DaylaneSettings.cs Services/SettingsService.cs Daylane.Tests/SettingsServiceTests.cs
git commit -S -m "Add a persisted settings store

Single-row JSON in SQLite, read through an immutable record that clamps
anything out of range. Writes merge over the stored object instead of
replacing it, so settings written by a newer build survive being run by
an older one."
```

---

### Task 9: Retire `config.ini` and make the idle threshold live

**Files:**
- Modify: `Services/IdleMonitor.cs:11-50`
- Modify: `Services/ForegroundTracker.cs:22`
- Modify: `Services/TrackingService.cs:30-36`
- Create: `Daylane.Tests/LegacyConfigImportTests.cs`

**Interfaces:**
- Consumes: `SettingsService` (Task 8).
- Produces:
  - `internal static int? LegacyConfig.ReadThresholdMinutes(string configPath)`
  - `internal static void IdleMonitor.Bind(SettingsService settings)` — replaces `IdleMonitor.Load()`.
  - `internal SettingsService DailyStatsStore.Settings { get; }`

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/LegacyConfigImportTests.cs`:

```csharp
using Daylane.Services;

namespace Daylane.Tests;

public class LegacyConfigImportTests
{
    private static string WriteConfig(string contents)
    {
        string path = Path.Combine(
            Path.GetTempPath(), "daylane-tests", Guid.NewGuid().ToString("N"), "config.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void ReadThresholdMinutes_ParsesValidValue()
    {
        string path = WriteConfig("[settings]\nthreshold_minutes=7\n");

        Assert.Equal(7, LegacyConfig.ReadThresholdMinutes(path));
    }

    [Theory]
    [InlineData("[settings]\nthreshold_minutes=0\n")]
    [InlineData("[settings]\nthreshold_minutes=999\n")]
    [InlineData("[settings]\nthreshold_minutes=abc\n")]
    [InlineData("[settings]\n")]
    public void ReadThresholdMinutes_RejectsInvalidValue(string contents)
    {
        string path = WriteConfig(contents);

        Assert.Null(LegacyConfig.ReadThresholdMinutes(path));
    }

    [Fact]
    public void ReadThresholdMinutes_WhenFileMissing_ReturnsNull()
    {
        Assert.Null(LegacyConfig.ReadThresholdMinutes(
            Path.Combine(Path.GetTempPath(), "daylane-tests", "definitely-absent", "config.ini")));
    }

    [Fact]
    public void LegacyConfigImported_DefaultsToFalseAndSurvivesAWrite()
    {
        using var temp = new TempDatabase();
        using (var connection = temp.Open())
        {
            Migrations.Apply(connection, temp.DatabasePath);
        }

        var service = new SettingsService(temp.ConnectionString);
        Assert.False(service.Current.LegacyConfigImported);

        service.Update(s => s with { LegacyConfigImported = true });

        Assert.True(new SettingsService(temp.ConnectionString).Current.LegacyConfigImported);
    }

    [Fact]
    public void LegacyConfigImported_OnceSet_StopsTheImportOverwritingAChosenValue()
    {
        using var temp = new TempDatabase();
        using (var connection = temp.Open())
        {
            Migrations.Apply(connection, temp.DatabasePath);
        }

        var service = new SettingsService(temp.ConnectionString);
        service.Update(s => s with { IdleThresholdMinutes = 15, LegacyConfigImported = true });

        // Re-reading must not reset a deliberately chosen value that happens to equal the default.
        Assert.Equal(15, new SettingsService(temp.ConnectionString).Current.IdleThresholdMinutes);
        Assert.True(new SettingsService(temp.ConnectionString).Current.LegacyConfigImported);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter LegacyConfigImportTests`
Expected: compile error — `LegacyConfig` does not exist.

- [ ] **Step 3: Extract the legacy parser**

Create `Services/LegacyConfig.cs` with the parsing currently inlined in `IdleMonitor.Load()` (`Services/IdleMonitor.cs:22-50`):

```csharp
using System.Globalization;

namespace Daylane.Services;

/// <summary>
/// Reads the pre-settings-store config.ini. Imported once into SettingsStore, after which
/// the file is dormant.
/// </summary>
internal static class LegacyConfig
{
    internal static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config.ini");

    internal static int? ReadThresholdMinutes(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return null;
            }

            foreach (string raw in File.ReadAllLines(configPath))
            {
                string line = raw.Trim();
                if (!line.StartsWith("threshold_minutes", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals >= 0
                    && int.TryParse(
                        line[(equals + 1)..].Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int parsed)
                    && parsed is >= IdleMonitor.MinThresholdMinutes and <= IdleMonitor.MaxThresholdMinutes)
                {
                    return parsed;
                }
            }
        }
        catch (IOException)
        {
        }

        return null;
    }
}
```

- [ ] **Step 4: Bind `IdleMonitor` to settings**

In `Services/IdleMonitor.cs`, replace `Load()` and the `ConfigPath` field with a live binding. `Threshold` keeps its type so `IsAway()` and `LastInputUtc()` are unchanged:

```csharp
    private static SettingsService? _settings;

    public static TimeSpan Threshold => TimeSpan.FromMinutes(
        _settings?.Current.IdleThresholdMinutes ?? DefaultThresholdMinutes);

    /// <summary>Reads the threshold live, so changing it in Settings needs no restart.</summary>
    public static void Bind(SettingsService settings) => _settings = settings;
```

In `Services/ForegroundTracker.cs:22`, replace `IdleMonitor.Load();` with nothing — the binding happens once in `TrackingService`'s constructor instead. Delete the now-unused line.

- [ ] **Step 5: Wire it up and run the one-time import**

In `Services/TrackingService.cs`, after `_store = new DailyStatsStore();` (`:32`):

```csharp
        Settings = new SettingsService(_store.ConnectionString);
        ImportLegacyConfigOnce();
        IdleMonitor.Bind(Settings);
```

Add the property and the import, which runs only while the stored value is still the default — so a user who has since changed the setting is never overwritten:

```csharp
    internal SettingsService Settings { get; }

    private void ImportLegacyConfigOnce()
    {
        if (Settings.Current.LegacyConfigImported)
        {
            return;
        }

        int? minutes = LegacyConfig.ReadThresholdMinutes(LegacyConfig.DefaultPath);
        Settings.Update(s => s with
        {
            IdleThresholdMinutes = minutes ?? s.IdleThresholdMinutes,
            LegacyConfigImported = true
        });
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 55 tests.

- [ ] **Step 7: Verify the app still detects idle**

Run: `dotnet run --project Daylane.csproj`
Confirm the Day view still shows an Active/Away status chip and the app starts without error.

- [ ] **Step 8: Commit**

```bash
git add Services/LegacyConfig.cs Services/IdleMonitor.cs Services/ForegroundTracker.cs Services/TrackingService.cs Daylane.Tests/LegacyConfigImportTests.cs
git commit -S -m "Move the idle threshold into settings and read it live

IdleMonitor loaded config.ini once into a static, which is why the
README said to restart after edits. It now reads through SettingsService,
so the value takes effect immediately. config.ini is imported once and
then dormant."
```

---

### Task 10: Theme tokens and dark palette

**Files:**
- Modify: `App.axaml:1-18` (theme dictionaries), and every style block holding a color literal
- Modify: `MainWindow.axaml:22,237-239,253`
- Create: `Daylane.Tests/ThemeTokenTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: 25 brush resources defined in both `ThemeVariant.Light` and `ThemeVariant.Dark` — the 11 existing token names plus `AppHoverBrush`, `AppSubtleBrush`, `AppSurfaceHoverBrush`, `AppThumbBrush`, `AppSegmentHoverBrush`, `AppAccentBorderBrush`, `AppAccentStrongBrush`, `AppGridMinorBrush`, `AppGridMajorBrush`, `AppIdleStripeBrush`, `AppIdleSoftStripeBrush`, `AppIntensity1Brush`, `AppIntensity2Brush`, `AppIntensity3Brush`.

- [ ] **Step 1: Write the failing test**

A missing dark value is invisible until someone switches theme and finds a white patch. This asserts both variants define every key, with no UI harness.

Create `Daylane.Tests/ThemeTokenTests.cs`:

```csharp
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace Daylane.Tests;

public class ThemeTokenTests
{
    private static readonly string[] RequiredTokens =
    [
        "AppBgBrush", "AppSurfaceBrush", "AppSurfaceAltBrush", "AppBorderBrush",
        "AppTextBrush", "AppMutedBrush", "AppAccentBrush", "AppAccentSoftBrush",
        "AppIdleBrush", "AppIdleSoftBrush", "AppRowHoverBrush", "AppHoverBrush",
        "AppSubtleBrush", "AppSurfaceHoverBrush", "AppThumbBrush", "AppSegmentHoverBrush",
        "AppAccentBorderBrush", "AppAccentStrongBrush", "AppGridMinorBrush",
        "AppGridMajorBrush", "AppIdleStripeBrush", "AppIdleSoftStripeBrush",
        "AppIntensity1Brush", "AppIntensity2Brush", "AppIntensity3Brush"
    ];

    public static TheoryData<string> Tokens()
    {
        var data = new TheoryData<string>();
        foreach (string token in RequiredTokens)
        {
            data.Add(token);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Tokens))]
    public void BothVariants_DefineToken(string token)
    {
        var app = new Daylane.App();
        AvaloniaXamlLoader.Load(app);

        foreach (var variant in (ThemeVariant[])[ThemeVariant.Light, ThemeVariant.Dark])
        {
            Assert.True(
                app.Resources.TryGetResource(token, variant, out object? value),
                $"{token} is not defined for {variant}.");
            Assert.IsAssignableFrom<IBrush>(value);
        }
    }
}
```

Add the Avalonia reference the test needs — `Daylane.Tests.csproj` already gets it transitively through the project reference, so no package edit is required.

**If `new App()` + `AvaloniaXamlLoader.Load` throws** because no Avalonia platform is
initialized (likely — `<FluentTheme />` may need a running app), you have a free choice of
mechanism, as long as the test still proves *both variants define all 25 tokens*:

- add `Avalonia.Headless.XUnit` and mark the tests `[AvaloniaTest]`; or
- skip the Avalonia runtime entirely and assert over `App.axaml` as XML — load the file,
  select each `ResourceDictionary` under `ThemeDictionaries` by its `x:Key` (`Light`,
  `Dark`), and assert every required token appears as a `SolidColorBrush x:Key` in both.

The XML route is the lighter of the two and tests exactly the property that matters. Pick
whichever actually runs; do not spend rounds fighting platform initialization.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter ThemeTokenTests`
Expected: fails from `AppHoverBrush` onward — those keys do not exist, and no variant-scoped dictionaries are defined.

- [ ] **Step 3: Replace the flat resource block with theme dictionaries**

In `App.axaml`, remove `RequestedThemeVariant="Light"` from the `<Application>` element (`:4`) — the variant comes from settings at runtime (Task 13). Replace the whole `<Application.Resources>` block (`:6-19`) with:

```xml
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.ThemeDictionaries>

                <ResourceDictionary x:Key="Light">
                    <SolidColorBrush x:Key="AppBgBrush" Color="#F4F5F7" />
                    <SolidColorBrush x:Key="AppSurfaceBrush" Color="#FFFFFF" />
                    <SolidColorBrush x:Key="AppSurfaceAltBrush" Color="#FAFBFC" />
                    <SolidColorBrush x:Key="AppBorderBrush" Color="#E5E7EB" />
                    <SolidColorBrush x:Key="AppTextBrush" Color="#111827" />
                    <SolidColorBrush x:Key="AppMutedBrush" Color="#6B7280" />
                    <SolidColorBrush x:Key="AppAccentBrush" Color="#2F9E6B" />
                    <SolidColorBrush x:Key="AppAccentSoftBrush" Color="#E8F7EF" />
                    <SolidColorBrush x:Key="AppIdleBrush" Color="#AEB4BE" />
                    <SolidColorBrush x:Key="AppIdleSoftBrush" Color="#EEF0F3" />
                    <SolidColorBrush x:Key="AppRowHoverBrush" Color="#F8FAFC" />
                    <SolidColorBrush x:Key="AppHoverBrush" Color="#EEF0F3" />
                    <SolidColorBrush x:Key="AppSubtleBrush" Color="#F3F4F6" />
                    <SolidColorBrush x:Key="AppSurfaceHoverBrush" Color="#F9FAFB" />
                    <SolidColorBrush x:Key="AppThumbBrush" Color="#FFFFFF" />
                    <SolidColorBrush x:Key="AppSegmentHoverBrush" Color="#E5E7EB" />
                    <SolidColorBrush x:Key="AppAccentBorderBrush" Color="#CDEBD9" />
                    <SolidColorBrush x:Key="AppAccentStrongBrush" Color="#DCF5E8" />
                    <SolidColorBrush x:Key="AppGridMinorBrush" Color="#F3F4F6" />
                    <SolidColorBrush x:Key="AppGridMajorBrush" Color="#E5E7EB" />
                    <SolidColorBrush x:Key="AppIdleStripeBrush" Color="#A0A7B1" />
                    <SolidColorBrush x:Key="AppIdleSoftStripeBrush" Color="#E4E7EB" />
                    <SolidColorBrush x:Key="AppIntensity1Brush" Color="#322F9E6B" />
                    <SolidColorBrush x:Key="AppIntensity2Brush" Color="#872F9E6B" />
                    <SolidColorBrush x:Key="AppIntensity3Brush" Color="#DC2F9E6B" />
                </ResourceDictionary>

                <ResourceDictionary x:Key="Dark">
                    <SolidColorBrush x:Key="AppBgBrush" Color="#0F1115" />
                    <SolidColorBrush x:Key="AppSurfaceBrush" Color="#171A20" />
                    <SolidColorBrush x:Key="AppSurfaceAltBrush" Color="#1C2027" />
                    <SolidColorBrush x:Key="AppBorderBrush" Color="#2A2F39" />
                    <SolidColorBrush x:Key="AppTextBrush" Color="#E5E7EB" />
                    <SolidColorBrush x:Key="AppMutedBrush" Color="#9AA3B2" />
                    <SolidColorBrush x:Key="AppAccentBrush" Color="#3DBE82" />
                    <SolidColorBrush x:Key="AppAccentSoftBrush" Color="#16301F" />
                    <SolidColorBrush x:Key="AppIdleBrush" Color="#5A616D" />
                    <SolidColorBrush x:Key="AppIdleSoftBrush" Color="#23272F" />
                    <SolidColorBrush x:Key="AppRowHoverBrush" Color="#1C2027" />
                    <SolidColorBrush x:Key="AppHoverBrush" Color="#23272F" />
                    <SolidColorBrush x:Key="AppSubtleBrush" Color="#1C2027" />
                    <SolidColorBrush x:Key="AppSurfaceHoverBrush" Color="#1F242B" />
                    <SolidColorBrush x:Key="AppThumbBrush" Color="#2A2F39" />
                    <SolidColorBrush x:Key="AppSegmentHoverBrush" Color="#2A2F39" />
                    <SolidColorBrush x:Key="AppAccentBorderBrush" Color="#1F4530" />
                    <SolidColorBrush x:Key="AppAccentStrongBrush" Color="#1C3D28" />
                    <SolidColorBrush x:Key="AppGridMinorBrush" Color="#1E222A" />
                    <SolidColorBrush x:Key="AppGridMajorBrush" Color="#2A2F39" />
                    <SolidColorBrush x:Key="AppIdleStripeBrush" Color="#6A717D" />
                    <SolidColorBrush x:Key="AppIdleSoftStripeBrush" Color="#2C313A" />
                    <SolidColorBrush x:Key="AppIntensity1Brush" Color="#323DBE82" />
                    <SolidColorBrush x:Key="AppIntensity2Brush" Color="#873DBE82" />
                    <SolidColorBrush x:Key="AppIntensity3Brush" Color="#DC3DBE82" />
                </ResourceDictionary>

            </ResourceDictionary.ThemeDictionaries>
        </ResourceDictionary>
    </Application.Resources>
```

- [ ] **Step 4: Replace every color literal in the styles with a token**

Still in `App.axaml`, work through the style blocks and swap each literal for `{DynamicResource ...}`:

| Literal | Selector(s) | Token |
|---|---|---|
| `#EEF0F3` | `Button.tab:pointerover` background | `AppHoverBrush` |
| `#111827` | `Button.tab:pointerover`, `Button.segment:pointerover` foreground | `AppTextBrush` |
| `#F3F4F6` | `Border.status-idle`, `Border.segment-group` background | `AppSubtleBrush` |
| `#F3F4F6` | `Button.secondary:pointerover` background | `AppSubtleBrush` |
| `White` | `Border.segment-thumb` background | `AppThumbBrush` |
| `#CDEBD9` | `Border.panel-accent` border | `AppAccentBorderBrush` |
| `#E5E7EB` | `Button.segment:pointerover` background | `AppSegmentHoverBrush` |
| `#F9FAFB` | `Button.date-picker:pointerover`, `Button.date-nav:pointerover` background | `AppSurfaceHoverBrush` |
| `#DCF5E8` | `ListBox.plain-list ListBoxItem:selected:pointerover` background | `AppAccentStrongBrush` |
| `#2F9E6B` | `Border.legend-swatch` in `MainWindow.axaml:218` | `AppAccentBrush` |

In `MainWindow.axaml`, replace `Background="White"` at `:22` and `:253` with
`Background="{DynamicResource AppSurfaceBrush}"`, and the three legend swatches at
`:237-239` with `AppIntensity1Brush`, `AppIntensity2Brush`, `AppIntensity3Brush`.

- [ ] **Step 5: Confirm no literals remain**

Run:

```bash
grep -nE '(Color|Background|Foreground|BorderBrush)="(#|White|Black)' App.axaml MainWindow.axaml | grep -v ThemeDictionaries
```

Expected: no output apart from the two theme dictionaries' own `Color="#..."` definitions.

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter ThemeTokenTests`
Expected: PASS, 25 cases.

- [ ] **Step 7: Commit**

```bash
git add App.axaml MainWindow.axaml Daylane.Tests/ThemeTokenTests.cs
git commit -S -m "Move every color into light and dark theme dictionaries

App.axaml defined 11 brush tokens and then hardcoded 15 more colors
inline, so a theme switch would have left white patches behind. All 25
are now variant-scoped, and a test asserts both variants define every
key."
```

---

### Task 11: Theme the timeline control

`Controls/TimelineBar.cs` is custom-drawn: it builds brushes from literals inside `Render`, including `static readonly Color` fields (`:661-664`) that cannot react to a theme change.

**Files:**
- Create: `Controls/TimelinePalette.cs`
- Modify: `Controls/TimelineBar.cs:156,168,397,417,423-424,433,452,540-548,656-677`
- Create: `Daylane.Tests/TimelinePaletteTests.cs`

**Interfaces:**
- Consumes: theme tokens (Task 10).
- Produces: `internal sealed record TimelinePalette` with `Text`, `Muted`, `Accent`, `GridMinor`, `GridMajor`, `Border`, `IdleFill`, `IdleStripe`, `IdleSoftFill`, `IdleSoftStripe` (all `Color`), plus `static TimelinePalette Resolve(IResourceHost host, ThemeVariant variant)`.

- [ ] **Step 1: Write the failing test**

The same platform-initialization caveat as Task 10 applies: if `new App()` plus
`AvaloniaXamlLoader.Load` will not run headlessly, resolve the palette against a
`ResourceDictionary` you build in the test (or use `Avalonia.Headless.XUnit`) rather than
against a full `App`. What must be proven is that `Resolve` returns the light values under
`ThemeVariant.Light` and the dark values under `ThemeVariant.Dark`.

Create `Daylane.Tests/TimelinePaletteTests.cs`:

```csharp
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Daylane.Controls;

namespace Daylane.Tests;

public class TimelinePaletteTests
{
    private static Daylane.App LoadedApp()
    {
        var app = new Daylane.App();
        AvaloniaXamlLoader.Load(app);
        return app;
    }

    [Fact]
    public void Resolve_UsesLightTokens()
    {
        var palette = TimelinePalette.Resolve(LoadedApp(), ThemeVariant.Light);

        Assert.Equal(Color.Parse("#111827"), palette.Text);
        Assert.Equal(Color.Parse("#2F9E6B"), palette.Accent);
        Assert.Equal(Color.Parse("#AEB4BE"), palette.IdleFill);
    }

    [Fact]
    public void Resolve_UsesDarkTokens()
    {
        var palette = TimelinePalette.Resolve(LoadedApp(), ThemeVariant.Dark);

        Assert.Equal(Color.Parse("#E5E7EB"), palette.Text);
        Assert.Equal(Color.Parse("#3DBE82"), palette.Accent);
        Assert.Equal(Color.Parse("#5A616D"), palette.IdleFill);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter TimelinePaletteTests`
Expected: compile error — `TimelinePalette` does not exist.

- [ ] **Step 3: Write the palette**

Create `Controls/TimelinePalette.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Daylane.Controls;

/// <summary>
/// The colors TimelineBar draws with, resolved from theme resources. Extracted from the
/// control so it can be unit-tested without a UI harness, and so a variant change is a
/// single cache rebuild rather than a hunt through Render.
/// </summary>
internal sealed record TimelinePalette
{
    public required Color Text { get; init; }
    public required Color Muted { get; init; }
    public required Color Accent { get; init; }
    public required Color GridMinor { get; init; }
    public required Color GridMajor { get; init; }
    public required Color Border { get; init; }
    public required Color IdleFill { get; init; }
    public required Color IdleStripe { get; init; }
    public required Color IdleSoftFill { get; init; }
    public required Color IdleSoftStripe { get; init; }

    internal static TimelinePalette Resolve(IResourceHost host, ThemeVariant variant) => new()
    {
        Text = Lookup(host, variant, "AppTextBrush"),
        Muted = Lookup(host, variant, "AppMutedBrush"),
        Accent = Lookup(host, variant, "AppAccentBrush"),
        GridMinor = Lookup(host, variant, "AppGridMinorBrush"),
        GridMajor = Lookup(host, variant, "AppGridMajorBrush"),
        Border = Lookup(host, variant, "AppBorderBrush"),
        IdleFill = Lookup(host, variant, "AppIdleBrush"),
        IdleStripe = Lookup(host, variant, "AppIdleStripeBrush"),
        IdleSoftFill = Lookup(host, variant, "AppIdleSoftBrush"),
        IdleSoftStripe = Lookup(host, variant, "AppIdleSoftStripeBrush")
    };

    private static Color Lookup(IResourceHost host, ThemeVariant variant, string key)
        => host.TryGetResource(key, variant, out object? value) && value is ISolidColorBrush brush
            ? brush.Color
            : Colors.Magenta; // Deliberately loud: a missing token should be visible, not silent.
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter TimelinePaletteTests`
Expected: PASS, 2 tests.

- [ ] **Step 5: Consume the palette in the control**

In `Controls/TimelineBar.cs`, add a cached palette that rebuilds on a variant change:

```csharp
    private TimelinePalette? _palette;

    private TimelinePalette Palette =>
        _palette ??= TimelinePalette.Resolve(this, ActualThemeVariant);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeVariantChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ActualThemeVariantChanged -= OnThemeVariantChanged;
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        _palette = null;
        InvalidateVisual();
    }
```

Then replace each literal with the palette field:

| Line | Literal | Replacement |
|---|---|---|
| `:156`, `:168` | `Color.Parse("#111827")` | `Palette.Text` |
| `:397` | `Color.Parse("#2F9E6B")` | `Palette.Accent` |
| `:417` | `Color.FromArgb(alpha, 47, 158, 107)` | `Color.FromArgb(alpha, Palette.Accent.R, Palette.Accent.G, Palette.Accent.B)` |
| `:423` | `Color.Parse("#F3F4F6")` | `Palette.GridMinor` |
| `:424`, `:433` | `Color.Parse("#E5E7EB")` | `Palette.GridMajor` |
| `:452`, `:548` | `Color.Parse("#6B7280")` | `Palette.Muted` |
| `:540` | `Color.Parse("#2F9E6B")` | `Palette.Accent` |

Delete the static color class at `:661-664` and take `Fill`, `Stripe`, `SoftFill` and
`SoftStripe` from `Palette.IdleFill`, `Palette.IdleStripe`, `Palette.IdleSoftFill` and
`Palette.IdleSoftStripe`. The idle-drawing helper at `:675-677` takes the two colors it
needs as parameters rather than reading statics.

- [ ] **Step 6: Confirm no literals remain in the control**

Run:

```bash
grep -nE 'Color\.(Parse|FromRgb)|Colors\.' Controls/TimelineBar.cs
```

Expected: no output.

- [ ] **Step 7: Verify the timeline still renders**

Run: `dotnet run --project Daylane.csproj`
Confirm the Day timeline draws with gridlines, hour labels, app bars and away stripes exactly as before.

- [ ] **Step 8: Commit**

```bash
git add Controls/TimelinePalette.cs Controls/TimelineBar.cs Daylane.Tests/TimelinePaletteTests.cs
git commit -S -m "Resolve timeline colors from the active theme

TimelineBar built brushes from 16 literals inside Render, four of them
static readonly fields that could never react to a variant change.
Colors now come from a cached palette rebuilt on
ActualThemeVariantChanged, and the intensity ramp derives its RGB from
the resolved accent instead of hardcoding it."
```

---

### Task 12: Dark native titlebar

`MainWindow` uses native chrome, so a dark app with a light titlebar looks broken.

**Files:**
- Create: `Services/WindowTheme.cs`
- Create: `Daylane.Tests/WindowThemeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `internal static void WindowTheme.Apply(Window window, bool dark)`.

- [ ] **Step 1: Write the failing test**

The P/Invoke itself cannot be asserted headlessly, so the test covers the part that has logic: an unrealized window (no handle) must be a safe no-op rather than a crash.

Create `Daylane.Tests/WindowThemeTests.cs`:

```csharp
using Avalonia.Controls;
using Daylane.Services;

namespace Daylane.Tests;

public class WindowThemeTests
{
    [Fact]
    public void Apply_WithoutPlatformHandle_DoesNotThrow()
    {
        var window = new Window();

        WindowTheme.Apply(window, dark: true);
        WindowTheme.Apply(window, dark: false);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter WindowThemeTests`
Expected: compile error — `WindowTheme` does not exist.

- [ ] **Step 3: Implement the titlebar attribute**

Create `Services/WindowTheme.cs`:

```csharp
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Daylane.Services;

/// <summary>
/// Darkens the native titlebar. MainWindow uses system chrome, so without this a dark
/// theme leaves a light caption bar above the app.
/// </summary>
internal static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    internal static void Apply(Window window, bool dark)
    {
        try
        {
            if (window.TryGetPlatformHandle()?.Handle is not IntPtr handle || handle == IntPtr.Zero)
            {
                return;
            }

            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Cosmetic and version-dependent; never worth failing a window for.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter WindowThemeTests`
Expected: PASS, 1 test.

- [ ] **Step 5: Commit**

```bash
git add Services/WindowTheme.cs Daylane.Tests/WindowThemeTests.cs
git commit -S -m "Add dark titlebar support via DWM

MainWindow uses native chrome, so dark mode needs
DWMWA_USE_IMMERSIVE_DARK_MODE or the caption bar stays light."
```

---

### Task 13: Settings view, theme application and tray reconciliation

**Files:**
- Create: `Services/ThemeSelector.cs`
- Modify: `ViewModels/MainWindowViewModel.cs:13,36,75-76,296-339`
- Modify: `MainWindow.axaml:32-50` (tab), plus a new settings panel
- Modify: `MainWindow.axaml.cs` (apply titlebar theme on open)
- Modify: `App.axaml.cs:35-70,139-174` (apply variant at startup, rewire the tray checkbox)
- Create: `Daylane.Tests/ThemeSelectorTests.cs`

**Interfaces:**
- Consumes: `SettingsService` (Task 8), `WindowTheme` (Task 12), `TrackingService.Settings` (Task 9).
- Produces:
  - `internal static ThemeVariant ThemeSelector.ToVariant(string appearance)`
  - `internal static bool ThemeSelector.IsDark(ThemeVariant actual)`
  - `AppTab.Settings` enum member; `MainWindowViewModel.IsSettingsSelected`, `SelectSettingsCommand`, and a bound property per setting.

- [ ] **Step 1: Write the failing test**

Create `Daylane.Tests/ThemeSelectorTests.cs`:

```csharp
using Avalonia.Styling;
using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class ThemeSelectorTests
{
    [Fact]
    public void ToVariant_MapsExplicitChoices()
    {
        Assert.Equal(ThemeVariant.Light, ThemeSelector.ToVariant(DaylaneSettings.AppearanceLight));
        Assert.Equal(ThemeVariant.Dark, ThemeSelector.ToVariant(DaylaneSettings.AppearanceDark));
    }

    [Fact]
    public void ToVariant_MapsSystemToDefault()
    {
        Assert.Equal(ThemeVariant.Default, ThemeSelector.ToVariant(DaylaneSettings.AppearanceSystem));
    }

    [Fact]
    public void ToVariant_FallsBackToDefault()
    {
        Assert.Equal(ThemeVariant.Default, ThemeSelector.ToVariant("neon"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter ThemeSelectorTests`
Expected: compile error — `ThemeSelector` does not exist.

- [ ] **Step 3: Write the selector**

Create `Services/ThemeSelector.cs`:

```csharp
using Avalonia.Styling;
using Daylane.Models;

namespace Daylane.Services;

internal static class ThemeSelector
{
    /// <summary>ThemeVariant.Default follows the OS and updates live, so "system" needs no
    /// listener of our own.</summary>
    internal static ThemeVariant ToVariant(string appearance) => appearance switch
    {
        DaylaneSettings.AppearanceLight => ThemeVariant.Light,
        DaylaneSettings.AppearanceDark => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };

    internal static bool IsDark(ThemeVariant actual) => actual == ThemeVariant.Dark;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter ThemeSelectorTests`
Expected: PASS, 3 tests.

- [ ] **Step 5: Add the Settings tab to the view model**

In `ViewModels/MainWindowViewModel.cs`, extend the enum at `:13`:

```csharp
internal enum AppTab
{
    Day,
    Insights,
    Settings
}
```

Add beside `IsDaySelected` / `IsInsightsSelected` (`:337-339`):

```csharp
    public bool IsSettingsSelected => SelectedTab == AppTab.Settings;
```

Add the command beside the two at `:75-76`:

```csharp
        SelectSettingsCommand = new RelayCommand(() => SelectedTab = AppTab.Settings);
```

Raise `IsSettingsSelected` wherever `IsDaySelected` and `IsInsightsSelected` are raised in the `SelectedTab` setter (`:296-336`).

Reach the settings service through the tracking service the view model already receives —
`TrackingService.Settings` exists as of Task 9, so keep the constructor at one parameter
(`MainWindowViewModel(TrackingService tracking)`) and assign
`_settings = tracking.Settings`. A second constructor parameter for something already
reachable is redundant.

Expose one property per setting. `Appearance` shown as three radio-style options; the rest
as toggles and numeric fields:

```csharp
    private readonly SettingsService _settings;

    public string Appearance
    {
        get => _settings.Current.Appearance;
        set
        {
            if (_settings.Current.Appearance == value)
            {
                return;
            }

            _settings.Update(s => s with { Appearance = value });
            OnPropertyChanged();
        }
    }

    public int IdleThresholdMinutes
    {
        get => _settings.Current.IdleThresholdMinutes;
        set
        {
            if (_settings.Current.IdleThresholdMinutes == value)
            {
                return;
            }

            _settings.Update(s => s with { IdleThresholdMinutes = value });
            OnPropertyChanged();
        }
    }
```

Repeat the same shape for `TrackingEnabled`, `ShowWindowOnAutoStart`, `MinimizeToTray` and `RetentionDays`. `AutoStart` goes through the registry first, because the registry is authoritative:

```csharp
    public bool AutoStart
    {
        get => StartupRegistration.IsEnabled();
        set
        {
            StartupRegistration.SetEnabled(value);
            _settings.Update(s => s with { AutoStart = StartupRegistration.IsEnabled() });
            OnPropertyChanged();
        }
    }
```

- [ ] **Step 6: Add the Settings panel to the window**

In `MainWindow.axaml`, add a third tab button beside the two at `:41-50`, following the same `Classes="tab"` pattern and binding `Command="{Binding SelectSettingsCommand}"`, with the sliding `TabThumb` widened to three positions in `MainWindow.axaml.cs`.

Add a panel at the same level as the Day and Insights grids (`:193`, `:107`), visible on
`IsSettingsSelected`, with three `Border Classes="panel"` sections:

- **Appearance** — three `RadioButton`s bound to `Appearance` with values `system`, `light`, `dark`.
- **Tracking** — `ToggleSwitch` for `TrackingEnabled`, `NumericUpDown` `Minimum="1" Maximum="240"` for `IdleThresholdMinutes`, `NumericUpDown` `Minimum="0"` for `RetentionDays` labelled so `0` reads as "keep forever".
- **Startup** — `ToggleSwitch` for `AutoStart`, `ShowWindowOnAutoStart` and `MinimizeToTray`.

Every control uses `{DynamicResource ...}` tokens only — no literals, or Task 10's grep gate fails.

- [ ] **Step 7: Apply the variant at startup and on change**

In `App.axaml.cs`, after `_trackingService` is constructed (`:37-40`) and before the window is shown:

```csharp
            var settings = _trackingService.Settings;
            RequestedThemeVariant = ThemeSelector.ToVariant(settings.Current.Appearance);
            settings.Changed += (_, current) => Dispatcher.UIThread.Post(() =>
            {
                RequestedThemeVariant = ThemeSelector.ToVariant(current.Appearance);
                if (_mainWindow is not null)
                {
                    WindowTheme.Apply(_mainWindow, ThemeSelector.IsDark(_mainWindow.ActualThemeVariant));
                }
            });
```

`new MainWindowViewModel(_trackingService)` is unchanged — the view model reads
`TrackingService.Settings` itself.

In `MainWindow.axaml.cs`, apply the titlebar once the handle exists:

```csharp
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowTheme.Apply(this, ThemeSelector.IsDark(ActualThemeVariant));
    }
```

Also handle `ActualThemeVariantChanged` here so following the OS updates the caption bar too.

- [ ] **Step 8: Rewire the tray startup checkbox**

The tray menu builds its own startup checkbox from `StartupRegistration` (`App.axaml.cs:153-166`). Route its click through `SettingsService` so the tray and the Settings view cannot disagree, and refresh `IsChecked` from `StartupRegistration.IsEnabled()` when the menu opens.

- [ ] **Step 9: Verify the whole surface switches**

Run: `dotnet run --project Daylane.csproj`

Check each of these:
- Settings tab opens and the highlight slides to it.
- Appearance → Dark: header, panels, timeline, gridlines, app rows, legend swatches and the native titlebar all go dark with no light patches.
- Appearance → System: matches the OS setting, and changing the OS theme updates the app live.
- Idle threshold to `1`: the Away chip appears after about a minute without input, with no restart.
- Toggling auto-start in Settings updates the tray checkbox, and vice versa.

- [ ] **Step 10: Commit**

```bash
git add Services/ThemeSelector.cs ViewModels/MainWindowViewModel.cs MainWindow.axaml MainWindow.axaml.cs App.axaml.cs Daylane.Tests/ThemeSelectorTests.cs
git commit -S -m "Add a Settings tab and apply the chosen theme

Appearance, tracking and startup settings in the UI, applied without a
restart. System follows the OS through ThemeVariant.Default. The tray
startup checkbox now shares one source of truth with the Settings view
instead of reading the registry independently."
```

---

### Task 14: Retention pruning

**Files:**
- Create: `Services/RetentionPruner.cs`
- Modify: `Services/TrackingService.cs` (run once at startup)
- Create: `Daylane.Tests/RetentionPrunerTests.cs`

**Interfaces:**
- Consumes: `LocalDate` columns (Task 4), `SettingsService` (Task 8).
- Produces: `internal static int RetentionPruner.Prune(SqliteConnection connection, int retentionDays, DateTime todayLocal)` — returns rows deleted.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/RetentionPrunerTests.cs`:

```csharp
using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class RetentionPrunerTests
{
    private static void InsertSegment(SqliteConnection connection, string localDate)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ActivitySegment
                (StartUtc, ProcessName, ExePath, DisplayName, IsIdle, KeyCount, MouseClickCount, LocalDate, LocalHour)
            VALUES ($date || 'T10:00:00.0000000Z', 'code', 'C:\Apps\code.exe', 'code', 0, 0, 0, $date, 10);
            """;
        command.Parameters.AddWithValue("$date", localDate);
        command.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ActivitySegment;";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void Prune_WithZeroDays_KeepsEverything()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);
        InsertSegment(connection, "2020-01-01");

        int deleted = RetentionPruner.Prune(connection, 0, new DateTime(2026, 9, 8));

        Assert.Equal(0, deleted);
        Assert.Equal(1L, Count(connection));
    }

    [Fact]
    public void Prune_DeletesOnlyRowsOlderThanWindow()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);
        InsertSegment(connection, "2026-09-08");
        InsertSegment(connection, "2026-09-01");
        InsertSegment(connection, "2026-06-01");

        int deleted = RetentionPruner.Prune(connection, 30, new DateTime(2026, 9, 8));

        Assert.Equal(1, deleted);
        Assert.Equal(2L, Count(connection));
    }

    [Fact]
    public void Prune_KeepsTheBoundaryDay()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);
        InsertSegment(connection, "2026-08-09");

        int deleted = RetentionPruner.Prune(connection, 30, new DateTime(2026, 9, 8));

        Assert.Equal(0, deleted);
        Assert.Equal(1L, Count(connection));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter RetentionPrunerTests`
Expected: compile error — `RetentionPruner` does not exist.

- [ ] **Step 3: Implement the pruner**

Create `Services/RetentionPruner.cs`:

```csharp
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter RetentionPrunerTests`
Expected: PASS, 3 tests.

- [ ] **Step 5: Run it at startup**

In `Services/TrackingService.cs`, after `IdleMonitor.Bind(Settings);`:

```csharp
        if (Settings.Current.RetentionDays > 0)
        {
            _store.PruneOldData(Settings.Current.RetentionDays);
        }
```

Add to `DailyStatsStore`:

```csharp
    internal void PruneOldData(int retentionDays)
    {
        lock (_dbWriteLock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            RetentionPruner.Prune(connection, retentionDays, DateTime.Now);
        }
    }
```

- [ ] **Step 6: Run the full suite**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 89 tests.

- [ ] **Step 7: Commit**

```bash
git add Services/RetentionPruner.cs Services/TrackingService.cs Services/DailyStatsStore.cs Daylane.Tests/RetentionPrunerTests.cs
git commit -S -m "Prune capture data past the retention window

Off by default (0 keeps history forever), so upgrading changes nothing
until the user opts in."
```

---

### Task 15: Update the README

The README documents behavior this sub-project changed, and promises Daylane's roadmap will break. It must not be left stale.

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: everything above.
- Produces: no code.

- [ ] **Step 1: Update the Features list**

Add dark mode and the settings UI:

```markdown
- Day timeline: foreground apps, Active/Away, input intensity
- Week and month insights. Month can take several seconds to open, longer on slower machines.
- Open-app time (visible windows, not only focus)
- Light and dark theme, or follow Windows
- Settings in-app; changes apply immediately
- Tray icon; optional Start with Windows
```

- [ ] **Step 2: Replace the Config section**

The current section documents `config.ini` and says "Restart after edits", which is no longer true:

```markdown
## Settings

Open the **Settings** tab. Changes apply immediately — no restart.

- **Appearance** — Light, Dark, or follow Windows
- **Away threshold** — minutes without keyboard or mouse input before a span is marked Away (1–240)
- **Retention** — delete records older than N days; `0` keeps everything
- **Startup** — start with Windows, start hidden in the tray, close button minimizes to tray

Settings live in `daylane.db`. The old `config.ini` is read once on
first launch after upgrading, to carry your `threshold_minutes` across;
after that the file is ignored and can be deleted.
```

- [ ] **Step 3: Note the database upgrade**

Add under Data and privacy:

```markdown
On first launch after upgrading, `daylane.db` is migrated and a backup of
the previous version is written beside it as `daylane.db.bak.v1`. Delete
it once you are satisfied the upgrade went cleanly.
```

Leave the "Not stored" list as it is. Window titles and screenshots are still not captured — sub-projects 2 and 6 change that, opt-in, and each will update this section when it lands.

- [ ] **Step 4: Verify the claims**

Re-read the README against the code. Confirm the threshold range still matches `IdleMonitor.MinThresholdMinutes`/`MaxThresholdMinutes` (1–240), and that the backup filename matches `Migrations.TryBackup`.

- [ ] **Step 5: Commit**

```bash
git add README.md
git commit -S -m "Document settings, dark mode and the database upgrade

config.ini and its restart-after-edits caveat are gone; the Settings tab
replaces them."
```

---

## Self-Review

**Spec coverage.** Every section of the design maps to a task:

| Spec section | Task(s) |
|---|---|
| §4 migration framework | 2 |
| §4 backup and rollback | 3 |
| §4 schema v2 activity columns, backfill, trigger | 4 |
| §4 `DailyInput` rebuild | 5 |
| §4 new tables, category seeds, group backfill | 6 |
| §4 `EnsureSelfDevice` | 7 |
| §5 settings model, store, unknown-key preservation | 8 |
| §5 `config.ini` import, live `IdleMonitor` | 9 |
| §6 token table, both variants | 10 |
| §6 `TimelinePalette`, intensity ramp | 11 |
| §6 native titlebar | 12 |
| §6 following the OS; §7 Settings view, tray reconciliation | 13 |
| §7 retention prune | 14 |
| §8 test project, path seam | 1 |
| §9 error handling | 3 (migration), 8 (settings write), 9 (config.ini), 12 (DWM) |
| §10 done criteria | 13 step 9, 14 step 6, 15 |

**Two gaps found and closed while reviewing:**

- The spec's §9 row for "database file locked or unwritable" is covered by Task 3's `InvalidOperationException` path only because `App.axaml.cs:35` already wraps `new TrackingService()` — verified the catch is `InvalidOperationException` and the constructor chain reaches `Migrations.Apply`, so no extra task is needed. Task 3's interface block now records this dependency explicitly.
- `GetTotalsForDate` would have silently summed across devices after Task 5's key change. Folded into Task 5 step 4 rather than left for sub-project 7 to discover.

**Type consistency.** `Migrations.Apply` keeps one signature across Tasks 2–7. `DaylaneSettings` property names in Task 8 match every binding in Task 13. `TimelinePalette` field names in Task 11 match the tokens defined in Task 10. `DeviceIdentity.Placeholder` and the `'local'` literal in Tasks 5–6 agree. `RetentionPruner.Prune` takes `todayLocal` in both its definition and its call site.

**Test count** rises across tasks 1–14: 1 → 5 → 8 → 14 → 17 → 32 → 35 → 47 → 55 → 80 (Task 10 contributes 25 theory cases) → 82 → 83 → 86 → 89. If a task's full-suite run reports fewer than its step says, a test was dropped rather than added — find it before moving on.
