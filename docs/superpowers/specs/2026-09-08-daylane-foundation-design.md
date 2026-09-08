# Daylane Foundation (Sub-project 1) — Design

**Date:** 2026-09-08
**Roadmap:** [2026-09-08-daylane-hindsight-parity-roadmap.md](./2026-09-08-daylane-hindsight-parity-roadmap.md)
**Status:** Awaiting review

## 1. Goal

Land the infrastructure every later sub-project depends on, plus the one user-visible
feature that belongs here: **dark mode**.

1. A real migration framework and schema v2 — the columns and tables that titles,
   taxonomy, screen memory and sync all need, added once.
2. A persisted settings store with a live-updating service, replacing `config.ini`.
3. A themeable color system with a full dark palette, and a Settings view to switch it.
4. A test project — Daylane currently has none, and migrations must not ship untested.

## 2. Non-goals

- **No AI. Ever.** No LLM engine, no embeddings, no model downloads, no AI reports or chat.
  This is a permanent product constraint, not a deferral.
- No window-title capture (#2), taxonomy UI (#3), export (#4), i18n (#5),
  screenshots (#6), or sync behavior (#7). This sub-project adds the *storage* those
  need, and nothing that writes to it.
- No macOS support.

## 3. Current state

| Fact | Location |
|---|---|
| Schema version 1, single `if (version < 1)` DDL block | `Services/DailyStatsStore.cs:11`, `:574` |
| 3 tables: `DailyInput`, `ActivitySegment`, `OpenAppSegment` | `Services/DailyStatsStore.cs:576-612` |
| `daylane.db` resolved from `AppContext.BaseDirectory` in a static method | `Services/DailyStatsStore.cs:477` |
| Timestamps stored as `DateTime.ToString("O")` UTC | `Services/DailyStatsStore.cs:646` |
| Only setting is `threshold_minutes` in `config.ini`, read once at startup | `Services/IdleMonitor.cs:17-50` |
| `RequestedThemeVariant="Light"` hardcoded | `App.axaml:4` |
| 11 brush tokens defined; ~15 more colors hardcoded inline | `App.axaml:7-18` |
| `Background="White"` hardcoded twice | `MainWindow.axaml:22`, `:253` |
| 16 hardcoded colors in custom render code | `Controls/TimelineBar.cs:156,168,397,417,423,424,433,452,540,543,548,661-664` |
| Native window chrome (no custom titlebar) | `MainWindow.axaml:1-19` |
| No test project, no `.sln` | — |

## 4. Component: migration framework

Replace the ad-hoc version check with an append-only script list, mirroring how Hindsight
tracks schema in `storage/migrations.rs`.

**New file `Services/Migrations.cs`:**

```csharp
internal static class Migrations
{
    // Index + 1 == schema version. Append only; never edit a shipped script.
    internal static readonly string[] Scripts = [V1, V2];

    internal static void Apply(SqliteConnection connection);
}
```

`Apply` reads `PRAGMA user_version`, then for each script above the current version runs
it inside a transaction and sets `PRAGMA user_version` in the same transaction, so a
failure leaves the database at the previous version rather than half-migrated.

`V1` is the existing DDL verbatim. Databases already at version 1 skip it, so existing
installs are untouched.

**Backup before upgrading.** Because Daylane is portable and the database sits next to the
executable, `Apply` copies `daylane.db` to `daylane.db.bak.v<n>` — where `<n>` is the
version being upgraded *from* — before running any script. This happens only when an
existing database is being upgraded (`user_version > 0` and the file exists); creating a
fresh database writes no backup. Cheap insurance; users can recover by renaming.

### Schema v2

**`ActivitySegment` — new columns.** Titles and browser host for #2; local date/hour for
cheap report windowing; the sync quartet so #7 is not a table rewrite.

```sql
ALTER TABLE ActivitySegment ADD COLUMN WindowTitle TEXT NULL;
ALTER TABLE ActivitySegment ADD COLUMN UrlHost     TEXT NULL;
ALTER TABLE ActivitySegment ADD COLUMN LocalDate   TEXT NULL;
ALTER TABLE ActivitySegment ADD COLUMN LocalHour   INTEGER NULL;
ALTER TABLE ActivitySegment ADD COLUMN DeviceId    TEXT NOT NULL DEFAULT 'local';
ALTER TABLE ActivitySegment ADD COLUMN RemoteId    TEXT NULL;
ALTER TABLE ActivitySegment ADD COLUMN UpdatedAt   TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z';
ALTER TABLE ActivitySegment ADD COLUMN Origin      TEXT NOT NULL DEFAULT 'local';
ALTER TABLE ActivitySegment ADD COLUMN Excluded    INTEGER NOT NULL DEFAULT 0;

UPDATE ActivitySegment
   SET LocalDate = date(StartUtc, 'localtime'),
       LocalHour = CAST(strftime('%H', StartUtc, 'localtime') AS INTEGER),
       UpdatedAt = COALESCE(EndUtc, StartUtc)
 WHERE LocalDate IS NULL;

CREATE INDEX IF NOT EXISTS IX_ActivitySegment_LocalDate
    ON ActivitySegment (LocalDate);
CREATE INDEX IF NOT EXISTS IX_ActivitySegment_LocalDateHour
    ON ActivitySegment (LocalDate, LocalHour);
CREATE UNIQUE INDEX IF NOT EXISTS UX_ActivitySegment_Device_Remote
    ON ActivitySegment (DeviceId, RemoteId);

CREATE TRIGGER IF NOT EXISTS TR_ActivitySegment_RemoteId
AFTER INSERT ON ActivitySegment WHEN NEW.RemoteId IS NULL
BEGIN
    UPDATE ActivitySegment SET RemoteId = NEW.Id WHERE Id = NEW.Id;
END;

-- The trigger is AFTER INSERT only, so rows that already existed at v1 would keep
-- RemoteId NULL forever and never dedupe on sync. Backfill them once, before the
-- unique index is relied on.
UPDATE ActivitySegment SET RemoteId = Id WHERE RemoteId IS NULL;
```

The trigger gives locally captured rows a `RemoteId` equal to their own `Id`, making
`(DeviceId, RemoteId)` the sync de-duplication key — Hindsight's approach, whose schema
doc notes *"Local rows get their own `id` via trigger."* SQLite treats NULLs as distinct
in unique indexes, so the index tolerates the brief NULL window between insert and trigger.

The one-time `UPDATE` above is easy to miss and matters: without it, every row captured
before the upgrade is invisible to sync de-duplication and would be re-pulled as a
duplicate on the first sync.

`'localtime'` applies the OS timezone per timestamp (correct across DST boundaries).
Rows captured on a machine in another timezone are interpreted with this machine's rules —
inherent to denormalizing a local date, and identical to what Hindsight does.

**`OpenAppSegment`** gets the same `LocalDate`, `LocalHour`, `DeviceId`, `RemoteId`,
`UpdatedAt`, `Origin` columns and the same backfill, trigger and indexes. Retrofitting
later is exactly what this sub-project exists to avoid.

**`DailyInput` — table rebuild.** Its primary key is `LogDate` alone, so two devices'
counts would collide on sync. SQLite cannot alter a primary key, so v2 rebuilds it:

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

**New tables.** Daylane's PascalCase naming, Hindsight's structure, minus the three
artifacts Hindsight's own docs mark dead (`activities.category_id`, `image_hash`,
`app_categories`).

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

Built-in categories are seeded (`work`, `code`, `browse`, `other`, `hidden`) with
`Builtin = 1`. `other` is the unclassified bucket and cannot be deleted.

**App-group backfill.** Every process name ever seen gets a single-member group whose
`Id` *is* the process name — deterministic, so two devices independently converge on the
same group id without coordination.

```sql
INSERT OR IGNORE INTO AppGroups (Id, DisplayName)
    SELECT DISTINCT ProcessName, DisplayName FROM ActivitySegment WHERE IsIdle = 0
    UNION
    SELECT DISTINCT ProcessName, DisplayName FROM OpenAppSegment;

INSERT OR IGNORE INTO AppGroupMembers (ProcessName, GroupId)
    SELECT Id, Id FROM AppGroups;
```

`IsIdle = 0` matters: Daylane writes a synthetic `ProcessName = 'Idle'` row for away spans
(`Models/ForegroundApp.cs`, `ForegroundApp.Idle`). Away time is not an app and must not
become a group.

### Post-migration step: device identity

Migration SQL cannot mint a UUID, so `DeviceId` defaults to the literal `'local'` and a
C# step reconciles it:

```csharp
internal static string EnsureSelfDevice(SqliteConnection connection);
```

On first run it generates a UUID, inserts a `Devices` row with `IsSelf = 1` and the machine
name as `DisplayName`, then rewrites `DeviceId = 'local'` to that UUID across
`ActivitySegment`, `OpenAppSegment` and `DailyInput`. Idempotent: later runs read the
existing self device and do nothing.

## 5. Component: settings

**Storage.** The single-row JSON table above, matching Hindsight's `settings_store`.

**`Models/DaylaneSettings.cs`** — an immutable record. Only foundation-level keys; each
later sub-project appends its own.

| Key | Default | Notes |
|---|---|---|
| `Appearance` | `"system"` | `system` \| `light` \| `dark` |
| `IdleThresholdMinutes` | `15` | Clamped 1–240, matching `IdleMonitor`'s existing bounds |
| `TrackingEnabled` | `true` | Master pause switch |
| `AutoStart` | `false` | Mirror of `StartupRegistration`. The registry is authoritative: on startup the stored value is reconciled from the registry (the user can remove the entry outside the app), and writes go to the registry first, then the store |
| `ShowWindowOnAutoStart` | `false` | Current behavior when launched at login |
| `MinimizeToTray` | `true` | Current close-button behavior (`MainWindow.axaml.cs:55`) |
| `RetentionDays` | `0` | `0` = keep forever, i.e. today's behavior |

**`Services/SettingsService.cs`:**

```csharp
internal sealed class SettingsService
{
    public DaylaneSettings Current { get; }
    public event EventHandler<DaylaneSettings>? Changed;
    public void Update(Func<DaylaneSettings, DaylaneSettings> mutate);
}
```

Three design points worth stating explicitly:

**Unknown keys are preserved.** `Update` reads the stored JSON into a `JsonNode`, merges
the serialized record over it, and writes the result back. Hindsight's `#[serde(default)]`
silently drops keys it does not know, so running an older build erases a newer build's
settings. Round-tripping through `JsonNode` means a downgrade preserves what it cannot read.

**`IdleMonitor` stops being static-mutable.** It currently loads `Threshold` once
(`IdleMonitor.Load()`) and the README says *"Restart after edits."* It becomes a
`SettingsService` reader, so changing the threshold takes effect immediately.

**`config.ini` is imported once, then ignored.** On the first v2 run, if `config.ini`
exists and carries a valid `threshold_minutes`, its value seeds `IdleThresholdMinutes`.
The file is then dormant — editing it has no effect. `README.md` must be updated to say
so, and to document the Settings view as the replacement.

**Serialization** uses a `JsonSerializerContext` source generator rather than reflection,
so the single-file release build stays safe if trimming is ever enabled.

## 6. Component: theme system and dark mode

**Tokenize first.** Every color moves into `ResourceDictionary.ThemeDictionaries` keyed by
`ThemeVariant.Light` and `ThemeVariant.Dark`, so a variant switch re-resolves every
`DynamicResource` with no per-control work. Existing token names are kept; the currently
hardcoded values get names.

| Token | Light | Dark | Currently |
|---|---|---|---|
| `AppBgBrush` | `#F4F5F7` | `#0F1115` | token |
| `AppSurfaceBrush` | `#FFFFFF` | `#171A20` | token |
| `AppSurfaceAltBrush` | `#FAFBFC` | `#1C2027` | token |
| `AppBorderBrush` | `#E5E7EB` | `#2A2F39` | token |
| `AppTextBrush` | `#111827` | `#E5E7EB` | token |
| `AppMutedBrush` | `#6B7280` | `#9AA3B2` | token |
| `AppAccentBrush` | `#2F9E6B` | `#3DBE82` | token |
| `AppAccentSoftBrush` | `#E8F7EF` | `#16301F` | token |
| `AppIdleBrush` | `#AEB4BE` | `#5A616D` | token |
| `AppIdleSoftBrush` | `#EEF0F3` | `#23272F` | token |
| `AppRowHoverBrush` | `#F8FAFC` | `#1C2027` | token |
| `AppHoverBrush` | `#EEF0F3` | `#23272F` | hardcoded |
| `AppSubtleBrush` | `#F3F4F6` | `#1C2027` | hardcoded |
| `AppSurfaceHoverBrush` | `#F9FAFB` | `#1F242B` | hardcoded |
| `AppThumbBrush` | `#FFFFFF` | `#2A2F39` | hardcoded (`White`) |
| `AppSegmentHoverBrush` | `#E5E7EB` | `#2A2F39` | hardcoded |
| `AppAccentBorderBrush` | `#CDEBD9` | `#1F4530` | hardcoded |
| `AppAccentStrongBrush` | `#DCF5E8` | `#1C3D28` | hardcoded |
| `AppGridMinorBrush` | `#F3F4F6` | `#1E222A` | hardcoded (TimelineBar) |
| `AppGridMajorBrush` | `#E5E7EB` | `#2A2F39` | hardcoded (TimelineBar) |
| `AppIdleStripeBrush` | `#A0A7B1` | `#6A717D` | hardcoded (TimelineBar) |
| `AppIdleSoftStripeBrush` | `#E4E7EB` | `#2C313A` | hardcoded (TimelineBar) |
| `AppIntensity1Brush` | `#322F9E6B` | `#323DBE82` | hardcoded (legend) |
| `AppIntensity2Brush` | `#872F9E6B` | `#873DBE82` | hardcoded (legend) |
| `AppIntensity3Brush` | `#DC2F9E6B` | `#DC3DBE82` | hardcoded (legend) |

The accent brightens from `#2F9E6B` to `#3DBE82` in dark so it keeps adequate contrast
against `#0F1115` while staying the same recognizable green.

**`Controls/TimelineBar.cs` needs real work.** It is custom-drawn and builds brushes from
literals inside `Render`, including a static palette of `static readonly Color` fields
(`:661-664`) that cannot react to a theme change. Plan:

- Extract `Controls/TimelinePalette.cs`: an immutable record of the colors the control
  draws with, plus `TimelinePalette Resolve(IResourceHost host, ThemeVariant variant)`.
- `TimelineBar` caches a palette, rebuilds it on `ActualThemeVariantChanged`, and calls
  `InvalidateVisual()`.
- The intensity ramp at `:417` and `:540-543` currently hardcodes the accent's RGB
  (`Color.FromArgb(alpha, 47, 158, 107)`). It takes its RGB from the resolved accent
  instead, so the ramp follows the theme.

The three alpha-prefixed legend swatches in `MainWindow.axaml:237-239` become the
`AppIntensity1/2/3Brush` tokens above — the same `#32`/`#87`/`#DC` alpha steps over
whichever accent the active variant defines.

**Native titlebar.** `MainWindow` uses native chrome, so a dark app with a light titlebar
looks broken. `Services/WindowTheme.cs` calls `DwmSetWindowAttribute` with
`DWMWA_USE_IMMERSIVE_DARK_MODE` (20) on the window handle, applied on open and on every
variant change. Failures are ignored — it is cosmetic and version-dependent.

**Following the OS.** `Appearance = "system"` maps to `ThemeVariant.Default`, which
Avalonia already resolves from OS colors and updates live; `light`/`dark` map to the
explicit variants. `App.axaml:4`'s hardcoded `RequestedThemeVariant="Light"` is removed and
set from settings at startup.

## 7. Component: Settings view

A third tab beside Day and Insights (`AppTab` in `ViewModels/MainWindowViewModel.cs:13`
gains a `Settings` member), following the existing sliding-highlight tab pattern.

Rows for the seven foundation settings, grouped: **Appearance** (theme selector),
**Tracking** (enabled, idle threshold, retention), **Startup** (auto-start, show window,
minimize to tray). Each writes through `SettingsService.Update` and takes effect
immediately. The tray menu's existing startup checkbox (`App.axaml.cs:153`) is rewired
through the same service so the two cannot disagree.

`RetentionDays > 0` enables a prune pass on startup that hard-deletes rows whose
`LocalDate` is older than the window, from `ActivitySegment`, `OpenAppSegment` and
`DailyInput`. These are the user's own capture data, not shared entities, so they are
deleted outright rather than tombstoned — nothing needs to propagate the deletion. It
defaults to `0` (keep forever), so behavior is unchanged unless the user opts in.

## 8. Component: test project

Daylane has no tests, and untested migrations against a portable database holding the
user's only copy of their history is not acceptable.

- `Daylane.Tests/Daylane.Tests.csproj`, `net10.0-windows`, xUnit.
- `Daylane.sln` at the root so `dotnet test` resolves both projects. The test project must
  not inherit the app's `RuntimeIdentifier` / `PublishSingleFile` Release properties —
  those are set in `Daylane.csproj`'s Release `PropertyGroup` and only apply to publish,
  but a test project carrying a RID trips restore, so it declares its own plain
  `net10.0-windows` target.
- `<InternalsVisibleTo Include="Daylane.Tests" />` in `Daylane.csproj` — the codebase is
  `internal` throughout.
- `DailyStatsStore` gains an optional constructor parameter for the database path
  (`DailyStatsStore(string? databasePath = null)`), keeping today's behavior as the
  default. Required for tests to use temp files; currently the path is resolved by a
  static method with no seam (`:477`).

**Tests to write, TDD, before the code:**

*Migrations* — fresh database reaches v2 with every table, index and trigger; a seeded v1
database upgrades with all rows preserved; `Apply` is idempotent; `LocalDate`/`LocalHour`
backfill matches `TimeZoneInfo.Local` conversion of `StartUtc`; the app-group backfill
excludes `Idle`; group ids equal process names; `DailyInput` rebuild preserves counts and
the new composite key; the trigger sets `RemoteId = Id` on local insert; a failing script
rolls back and leaves `user_version` unchanged; the `.bak` file is created.

*Settings* — empty `'{}'` yields documented defaults; round-trip preserves every field;
unknown keys survive a write; `IdleThresholdMinutes` clamps at 1 and 240;
`config.ini` import runs once and only with a valid value; `Changed` fires on update.

*Device identity* — `EnsureSelfDevice` mints one UUID, is idempotent, and rewrites every
`'local'` row across all three tables.

*Theme* — the token map defines every key in both variants (a data-level check that
catches a missing dark value), and `TimelinePalette.Resolve` returns the dark values under
`ThemeVariant.Dark`. No UI harness required, which is why palette resolution is extracted
from the control.

*Retention* — prunes only rows older than the window; `0` is a no-op.

## 9. Error handling

| Failure | Behavior |
|---|---|
| Migration script throws | Transaction rolls back, `user_version` unchanged, `.bak` retained. The app shows a dialog naming the backup file and exits rather than running against a database it does not understand. |
| Database file locked or unwritable | Same dialog-and-exit path. Today `DailyStatsStore`'s constructor would throw into startup unhandled. |
| Settings write fails | Keep the in-memory value, log, do not crash. Settings are not worth losing a session over. |
| `config.ini` unreadable | Ignore and use defaults — matches the existing `catch (IOException)` at `IdleMonitor.cs:43`. |
| `DwmSetWindowAttribute` fails | Ignored; cosmetic only. |

## 10. Done criteria

- `dotnet test` green; `dotnet build` and `dotnet publish -c Release` both clean.
- A v1 database from the current release upgrades to v2 with no row loss, verified against
  a real captured database.
- Toggling Appearance switches every surface — including the timeline control and the
  native titlebar — with no restart and no light-colored leftovers.
- Changing the idle threshold in Settings takes effect without a restart.
- `README.md` updated: Settings view documented, `config.ini` marked legacy, dark mode
  listed under Features.
