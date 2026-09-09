# Daylane Foundation: Decisions Taken During Implementation

**Date:** 2026-09-08 to 2026-09-09
**Branch:** `worktree-daylane-foundation`
**Spec:** [2026-09-08-daylane-foundation-design.md](./2026-09-08-daylane-foundation-design.md)
**Plan:** [../plans/2026-09-08-daylane-foundation.md](../plans/2026-09-08-daylane-foundation.md)

Every judgement call made while executing the 15-task plan, recorded so they can be
reviewed and reversed. Rulings that only existed in a scratch ledger would have been
decisions made in secret.

Outcome: 33 commits, 46 files, +3567/−264, **124 tests**, 15 task reviews, 12 fix rounds.

---

## 1. Corrections to the plan itself

These are places the plan was wrong and the code was right. All were caught by reviewers or
by running the app, not by the plan's own authority.

| Ruling | Why | Cost if wrong |
|---|---|---|
| **`LocalDate`/`LocalHour` must be written on capture, not only backfilled.** The plan specified the columns, the backfill and two indexes, and never the write path. | `NULL < 'cutoff'` is `NULL`, so retention was inert for all new data, both indexes indexed nothing, and sub-project 4's whole rationale (report windows filter on `LocalDate`) rested on a column that was always NULL. | Recoverable by a v3 `UPDATE`; none, as taken. |
| **`DeviceId`/`UpdatedAt` must be written on segment capture too.** Same defect one layer out, found by the final review. | The app wrote worse data than its own migration: `'local'` and an epoch timestamp forever, while pre-upgrade rows were correct. Sub-project 7 would have inherited the backfill this branch existed to avoid. | Recoverable by a v3 `UPDATE`. |
| **The spec's "database locked or unwritable → dialog and exit" was not met.** I had recorded it as satisfied. | `SqliteException` derives from `DbException`, not `InvalidOperationException`, so `connection.Open()`, the WAL pragma, `ReadUserVersion` and `EnsureSelfDevice` all escaped the catch. My reasoning — that reaching `Migrations.Apply` made every throw in the chain that type — was simply wrong. | A broader catch on a path that already terminates the app. |
| **The failure message promised a backup that may not exist.** Gated on `databasePath is not null` when a backup is only written for `version > 0`. | An error dialog naming a nonexistent recovery file is worse than one naming none — it sends the user hunting at the worst moment. | A longer signature on a private helper. |
| **"The database was left unchanged" could be false.** `version` captured once, but each script commits separately. | Unreachable until V2 landed; a fresh database then runs two scripts, and a V2 failure would have produced exactly that false claim. | One extra local. |
| **`config.ini` parsing changed behaviour in the move.** The original let the *last* valid `threshold_minutes` win; the new one returned on the first. | Narrow, but this is the one path whose whole job is honouring files written by older versions. | None; it restores pre-existing behaviour. |
| **README said "Away threshold"; the control says "Idle threshold (minutes)".** My replacement copy invented a label. Three further label mismatches were then found in the same section. | A user scanning Settings for a control that does not exist. | None. |
| **A blanket `s/Path_/DatabasePath/g` over the plan corrupted a SQL index name** (`ExePath_Start` → `ExeDatabasePathStart`). | Caught by the implementer, which used the real shipped DDL. Future plan-wide renames get a scoped pattern. | None; caught before reaching a database. |

## 2. Scope and process

- **Extending the V2 script across Tasks 4–6 is legitimate.** Append-only governs *released*
  scripts; V2 never shipped. The real hazard was local: a development database already at
  v2 will not re-run an extended V2. Carried as a warning in each dispatch.
- **Tasks 5 and 6's later findings entered the task that surfaced them**, not a reopened
  earlier task, because that loop was still open and the dependent work was blocked.
- **The `LocalDate` fix went into Task 14's loop rather than reopening Task 4**, for the same
  reason.
- **The final fix wave stayed one dispatch, not six.** The first attempt stalled during
  exploration; the remedy was removing the latitude to explore, not reducing scope, since
  every fix already had a file:line anchor.
- **Task 13 escalated to a fresh implementer on a more capable model after a stall**, per the
  fix-loop rules. Its diagnosis was salvaged from an abandoned file rather than re-derived —
  though that diagnosis later proved partly wrong (see §4).
- **Task 13's fix round 4 resumed the same implementer rather than escalating**, deviating
  from the rule: the loop was converging on *different* findings each round, and the model
  was already the most capable available.

## 3. Testing rulings

The recurring theme: **a test that constructs the state it is meant to verify proves nothing
about production.** This cost the branch two defects before it was internalised.

- **Retention tests must drive the real capture path.** All three original tests hand-populated
  `LocalDate` in raw `INSERT`s — which is exactly why a feature that could not touch
  production data still passed.
- **The theme regression test must resolve through a real attach**, not a hand-built
  `ResourceDictionary`. The older test proved `Resolve` works *given* a host holding the
  tokens; it never proved a real control could reach them.
- **The `config.ini` import guard was extracted to `LegacyConfig.ImportOnce`** so it could be
  tested at all — `TrackingService`'s constructor builds real Win32 hooks.
- **The `DailyInput` upsert needed a permanent accumulate test**; the only thing that ever
  exercised it was an ad hoc check deleted before commit.
- **Task 6's tombstone defaults are pinned by one table-driven test** comparing against a
  single `const`, so a typo in the test cannot agree with a typo in the schema.
- **Theme tests may use any mechanism that actually runs** (headless Avalonia, or XML
  assertion over `App.axaml`) rather than being pinned to an approach that needs a platform.
- **No test was written for the titlebar repaint or the `wal_checkpoint` busy branch.** Both
  need a real window or a concurrent reader; a test that cannot fail for the real reason is
  worse than none.

## 4. Code rulings

- **Defence in depth over relying on an invariant:** `WHERE ProcessName <> 'Idle'` added to the
  `OpenAppSegment` backfill branch, which had no `IsIdle` column and relied on an unenforced
  assumption about `OpenAppTracker`.
- **`EnsureSelfDevice`'s check-then-act race closed** with an immediate transaction, so its
  idempotency no longer depends on the single-instance mutex — sub-project 7 will construct
  stores.
- **The WAL is checkpointed before the migration backup**, and a `busy != 0` checkpoint now
  reports a failed backup rather than a complete one.
- **Tracker caches cleared on pause**, so resuming in the same app opens a new segment. Without
  this, pausing and resuming while staying in one app silently recorded nothing while the
  header read "Live".
- **Three settings that persisted but did nothing were wired up** (`TrackingEnabled`,
  `MinimizeToTray`, `ShowWindowOnAutoStart`) rather than shipped inert. A control that does
  nothing is worse than an absent one.
- **`Normalize()` references `IdleMonitor`'s constants** rather than duplicating `1` and `240`.
- **`TimelinePalette.Surface` added** for two `Brushes.White` fills that evaded the plan's own
  grep gate, which matched `Color.Parse|Colors.` but not `Brushes.*`.
- **The startup prune runs after `CloseOrphanOpenSegments`** and is wrapped in try/catch, so a
  crash-orphaned segment is closed before it can be deleted and a locked file cannot block
  startup.
- **The titlebar repaint is gated on `Appearance` actually changing**, so typing in a numeric
  field no longer forces three non-client redraws.
- **`SettingsService.Write` upserts** rather than `UPDATE ... WHERE Id = 1`, which would
  silently no-op if the seeded row were ever absent.
- **`TempDatabase.Dispose` keeps swallowing cleanup failures**, but the comment claiming a leak
  "should fail the test that leaked it" was corrected — throwing from a test helper's
  `Dispose` would mask the assertion failures the test exists to report.
- **`MainWindowViewModel` keeps one constructor parameter**, reading `tracking.Settings`.

## 5. Verification rulings

- **The app was never launched while the user's own Daylane was running.** `Program.cs`'s mutex
  name is a hardcoded constant, so a worktree build does not fail harmlessly — it pops the
  user's live window to the foreground. Implementers were forbidden from launching it or from
  editing `MutexName` to work around it.
- **Dark mode was verified by screenshot, not by assertion.** This found the timeline rendering
  entirely magenta — a Critical defect that 107 green tests and three reviews had missed.
- **The tracker pause/resume fix was verified in the running app**, because the earlier
  screenshot pass had proven rendering while saying nothing about lifecycle.

## 6. Deferred, deliberately

Recorded so they are not rediscovered as surprises.

- **Retention never `VACUUM`s**, so the database file never shrinks — SQLite reuses freed pages
  instead. "Keep history for (days)" reads as a disk-footprint control but is not one.
- **`OpenAppSegment` has `LocalHour` written but no composite index**, while `ActivitySegment`
  has both. Adding one before sub-project 4 knows its query shape would be guessing.
- **`NumericUpDown` commits per keystroke**, so typing "240" writes the settings store three
  times. Much reduced now the titlebar repaint is gated.
- **Retention deletes all devices' rows, not just this device's.** Retention is a local-storage
  concern and sync does not exist yet; revisit in sub-project 7.
- **`AppGroupMembers.GroupId REFERENCES AppGroups(Id)` is decorative** — foreign keys are never
  enabled on any connection. Sub-project 3 should know before relying on it.
- **`RemoteId` is `TEXT` and relies on implicit numeric↔text coercion.** Sub-project 7 must
  compare `RemoteId = '5'`, not `= 5`.
- **`AppBorderBrush` vs `AppSurfaceBrush` is ~1.3:1 in dark.** The shipping light pair is
  ~1.17:1, so this mirrors an accepted characteristic rather than introducing a regression.
- **Nothing reconciles the stored `AutoStart` against the registry**, but every read goes to the
  registry directly, so the stored key is write-only and cannot mislead.
