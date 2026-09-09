using Microsoft.Data.Sqlite;

namespace Daylane.Services;

/// <summary>
/// Append-only schema migrations. Index + 1 == schema version, tracked in PRAGMA user_version.
/// Never edit a shipped script; add a new one.
/// </summary>
internal static class Migrations
{
    internal static readonly string[] Scripts = [V1, V2, V3];

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

        // Only back up when upgrading data that already exists. A fresh database has
        // nothing to lose, and writing a .bak of an empty file just litters the folder.
        bool backedUp = version > 0
            && databasePath is not null
            && File.Exists(databasePath)
            && TryBackup(connection, databasePath, version);

        int appliedThrough = version;

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

                // user_version lives in the database header and is transactional, so a
                // rollback leaves the version where it was.
                using (var setVersion = connection.CreateCommand())
                {
                    setVersion.Transaction = transaction;
                    setVersion.CommandText = $"PRAGMA user_version = {i + 1};";
                    setVersion.ExecuteNonQuery();
                }

                transaction.Commit();
                appliedThrough = i + 1;
            }
            catch (SqliteException ex)
            {
                transaction.Rollback();

                string backupNote = backedUp
                    ? $" A backup of the previous database was kept at \"{databasePath}.bak.v{version}\"."
                    : string.Empty;

                // A multi-script Apply commits each script's transaction as it goes, so a
                // later script can fail after an earlier one already advanced the schema.
                // Say which version the database is actually sitting at rather than always
                // claiming nothing changed.
                string stateNote = appliedThrough == version
                    ? " The database was left unchanged."
                    : $" The database was upgraded to version {appliedThrough} before the failure and left there.";

                throw new InvalidOperationException(
                    $"Daylane could not upgrade its database to version {i + 1}: {ex.Message}"
                    + backupNote
                    + stateNote,
                    ex);
            }
        }
    }

    private static bool TryBackup(SqliteConnection connection, string databasePath, int fromVersion)
    {
        try
        {
            // Production runs in WAL mode, so committed rows from a session that ended
            // without a clean checkpoint (a crash, a killed process) can still be sitting
            // in a leftover -wal sidecar that a bare file copy would never see. TRUNCATE
            // folds that sidecar back into the main file so the .bak is self-contained.
            //
            // A blocked checkpoint does not throw: it returns a row whose first column, busy,
            // is non-zero and leaves the sidecar in place. Reading that column is the only way
            // to tell a complete checkpoint from one that quietly gave up, and a .bak taken
            // after a busy checkpoint can be missing the most recent committed rows.
            using (var checkpoint = connection.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                if (checkpoint.ExecuteScalar() is long busy && busy != 0)
                {
                    return false;
                }
            }

            File.Copy(databasePath, $"{databasePath}.bak.v{fromVersion}", overwrite: true);
            return true;
        }
        catch (SqliteException)
        {
            // The checkpoint failed, so we cannot guarantee the copy below would be
            // complete. Don't take a backup we can't vouch for.
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A backup we cannot write must not block an upgrade the user needs.
            return false;
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
        -- OpenAppSegment has no IsIdle column, so the same exclusion is enforced here by
        -- name instead of relying on OpenAppTracker never emitting one.
        INSERT OR IGNORE INTO AppGroups (Id, DisplayName)
            SELECT ProcessName, MIN(DisplayName) FROM ActivitySegment
             WHERE IsIdle = 0 GROUP BY ProcessName
            UNION
            SELECT ProcessName, MIN(DisplayName) FROM OpenAppSegment
             WHERE ProcessName <> 'Idle' GROUP BY ProcessName;

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
        """;

    // v3: title search. WindowTitle is written from sub-project 2 onward; this index serves
    // the search query, which always filters by LocalDate first.
    private const string V3 = """
        CREATE INDEX IF NOT EXISTS IX_ActivitySegment_LocalDate_Title
            ON ActivitySegment (LocalDate, WindowTitle);
        """;
}
