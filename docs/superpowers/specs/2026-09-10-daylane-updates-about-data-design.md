# Daylane sub-project 8: Updates, About & data management — design

**Date:** 2026-09-10
**Status:** Approved (scope + design)
**Roadmap:** `docs/superpowers/specs/2026-09-08-daylane-hindsight-parity-roadmap.md` (#8)
**Source survey:** Hindsight's implementation was read before this was written; findings and
file:line citations are in the branch's `.superpowers/research/hindsight-sp8-survey.md`.

## 1. What this delivers

Three features the 2026-09-09 roadmap audit found missing, all reachable from Settings:

1. **Update checking** — opt-in, off by default. Tells you a newer release exists and links to it.
2. **Data management** — purge recorded activity, see how much disk the database uses, see where it lives.
3. **About** — version, author, license, repo and issue links.

Nothing here is AI-related, and nothing here may become so.

## 2. Decisions, and where they diverge from Hindsight

Hindsight was surveyed rather than copied. Four decisions differ deliberately.

| Decision | Hindsight | Daylane | Why |
|---|---|---|---|
| Auto update check | **On by default**, `autoUpdateEnabled = true`, `autoUpdateInterval = "weekly"`; a fresh install contacts GitHub on first launch with no opt-in | **Off by default.** Nothing is contacted until the user turns it on | The README promises "No accounts, no cloud". A default install must keep that promise literally, not approximately |
| Check schedule | daily / weekly / monthly / on-startup, with a stored last-checked timestamp | **Once per launch, plus a Check now button.** No interval setting, no stored schedule state | An activity tracker runs for weeks. An interval buys almost nothing over once-per-launch and costs a settings row, a timestamp to persist, and a class of clock bugs |
| Install | Fully automatic in-app: `downloadAndInstall()` then `relaunch()` | **Link out to the release page.** Daylane never downloads or executes anything | .NET has no `tauri-plugin-updater` equivalent. Building a self-updater means code signing, elevation and a restart dance; a link is honest and cannot corrupt an install |
| Release source | GitHub `latest.json` endpoint | **GitHub Releases API for `Marin-Kitagawa/Daylane`** | This repo is a fork of `mirbyte/Daylane`, and the shipped build contains sub-projects 1 and 2, which upstream does not. Pointing at upstream would offer users an "update" that is a downgrade |

Two further notes from the survey, both acted on:

- Hindsight's purge confirmation reads *"the next sync will re-pull all history from the cloud"* even for a
  user who has never configured sync — false for most readers. Daylane's copy says only what is true of
  Daylane: the data is deleted from this machine and is not recoverable.
- Hindsight's purge gained a `VACUUM` only in v0.6.7, because without it "clear data" left 20+ MB behind and
  looked broken. Daylane does it from the start, and for the same reason: SQLite does not return freed pages
  to the filesystem on `DELETE`, so a purge without `VACUUM` reports no space reclaimed.

## 3. Update checking

### 3.1 Settings

Two new keys on `DaylaneSettings`, both persisted in the existing single-row JSON store:

| Key | Type | Default | Meaning |
|---|---|---|---|
| `CheckForUpdates` | `bool` | **`false`** | When false, no update code ever touches the network |
| `LastSeenVersion` | `string?` | `null` | The newest tag the user has already been shown, so the banner does not nag |

`LastSeenVersion` is **not** a last-checked timestamp. It records what the user has seen, not when we looked —
the only thing needed to avoid re-announcing the same release, and it carries no clock-skew failure modes.

### 3.2 Behaviour

- **Disabled (default):** no request is made, ever. Not on startup, not on a timer. The Check now button is
  still available and performs a single check when pressed — pressing a button is an explicit act, so it does
  not require the toggle. This is stated in the UI so it cannot surprise anyone.
- **Enabled:** one check per launch, on a background thread, after the window is up. Never on the UI thread.
- A check compares the running assembly version against the newest non-draft, non-prerelease release tag.
- If newer, the Settings panel shows the new version and a link to its release page. If not, it says so.
- Every failure — offline, rate-limited, DNS, malformed JSON, GitHub down — is a silent no-op in the UI's
  normal state and a one-line status in the panel. **An update check must never produce a dialog, never block
  startup, and never write to the database.**

### 3.3 What is sent

A single unauthenticated HTTPS `GET` to
`https://api.github.com/repos/Marin-Kitagawa/Daylane/releases/latest`, carrying only a `User-Agent` of
`Daylane/<version>` (GitHub rejects requests without one) and `Accept: application/vnd.github+json`.

No account, no token, no identifier, no telemetry, no crash reporting. GitHub will observe the requesting IP
and the version string in the User-Agent, as it would for any download. **The README must say exactly this**,
in those terms, including the version-in-User-Agent detail, because that is the one piece of information a
reader would not otherwise expect.

Hindsight has no telemetry, analytics or crash reporting anywhere in its codebase. Daylane keeps it that way.

### 3.4 Version comparison

Release tags are compared as versions, not strings, so `v1.0.10` is correctly newer than `v1.0.9`. A leading
`v` is tolerated on either side. A tag that does not parse as a version is treated as "no update available"
rather than as an error — a malformed tag upstream must not surface as a scary message to a user who can do
nothing about it.

The comparison uses the assembly's informational version, and `Daylane.csproj` gains `Authors` and `Product`
so About has something authoritative to display.

## 4. Data management

### 4.1 Purge recorded activity

One button, one confirmation, one honest description.

**Deleted:** `ActivitySegment`, `OpenAppSegment`, `DailyInput`, `ProcessPaths`, `AppIcons`, `SyncOutbox`, and
`SyncCursor` is reset. These are recorded activity and caches derived from it.

**Never touched:** `SettingsStore`, `Devices`, `AuthState`, `Categories`, `SuperCategories`, `AppGroups`,
`AppGroupMembers`. Settings, device identity and taxonomy survive a purge. Hindsight states this split
explicitly in a doc comment and it is worth stating as explicitly here: a user clearing their history does not
expect to also lose their preferences.

**Sequence, and why the order is load-bearing:**

1. Close any open segment first. Purging while a segment is open would delete the row the tracker still holds
   an id for, and the next flush would silently write counts against a row that no longer exists.
2. Delete inside one transaction, under the store's existing `_dbWriteLock`, so a concurrent capture cannot
   interleave.
3. `VACUUM` **after** committing, in a separate command. SQLite forbids `VACUUM` inside a transaction.
4. Reset the tracker's in-memory counters and republish, so the UI shows zero rather than yesterday's totals
   until the next tick.

**Confirmation copy** must state that it is permanent and local, and must not mention sync, cloud or recovery.

Purging screenshots is **out of scope** — Daylane has no screenshot store until sub-project 6. It will add its
own purge alongside the capture it introduces.

### 4.2 Storage usage

Two numbers and a path:

- **Database size** — the sum of `daylane.db`, `daylane.db-wal` and `daylane.db-shm`. Hindsight stats the
  single database file; that understates a WAL database, sometimes badly, because an unchecked-pointed WAL can
  exceed the main file. Reporting the total is the honest figure.
- **Rows recorded** — a count across the capture tables, so the number means something to a user who has no
  intuition for megabytes.
- **Data path** — displayed, selectable, and openable in Explorer. **Not editable.** Daylane keeps
  `daylane.db` next to the executable and is portable by design; Hindsight's editable path only rewrites a
  pointer and deliberately does not migrate existing data, which its own comment justifies as the lesser evil.
  Offering a path field that silently orphans history is worse than not offering one.

Storage figures are read on demand and when the panel becomes visible — not on a poll. Hindsight polls every
30 seconds; for two `stat` calls behind a settings tab nobody is looking at, that is waste.

The description states that Daylane prunes automatically when a retention window is set, and keeps everything
when it is not. This differs from Hindsight, whose copy says "The database is never auto-cleaned" — Daylane's
`RetentionPruner` has done exactly that since sub-project 1, so copying that sentence would be a lie.

## 5. About

Static, offline, and correct:

- **Version** — the assembly's informational version.
- **Author** — from `Daylane.csproj`'s `Authors`.
- **License** — **AGPL-3.0**, linking to the repository's `LICENSE`. Note this differs from Hindsight's MIT;
  the license shown must be Daylane's own.
- **Repository** — `https://github.com/Marin-Kitagawa/Daylane`.
- **Report an issue** — that repository's issues page.
- One line naming Hindsight as the project this port draws its feature set from, with a link. It is the honest
  provenance for a feature-parity port, and costs nothing.

The README badges currently point at `mirbyte/Daylane`, the upstream this repo forked from, so the download
count and latest-release badges describe a different build than the one being shipped. They are repointed to
this fork as part of this sub-project.

Links open in the system browser via the OS handler. No embedded browser, no HTTP client.

## 6. Error handling

| Condition | Behaviour |
|---|---|
| Update check with no network | Silent; panel shows "Could not reach GitHub" |
| GitHub rate limit (60/hour unauthenticated) | Treated as a failed check, not an error state |
| Malformed or missing `tag_name` | "No update available" |
| Tag that does not parse as a version | "No update available" |
| Purge fails mid-transaction | Transaction rolls back; nothing is half-deleted; the panel reports the failure |
| `VACUUM` fails after a successful delete | Purge is still reported as succeeded, because it was — the data is gone. Space reclamation is noted as pending |
| Storage `stat` fails (file locked) | That figure shows as unavailable rather than zero. **Zero is a claim; unknown is the truth** |

## 7. Testing

Every pure decision gets a unit test that drives the real production function, not a re-implementation of it.
Sub-projects 1 and 2 shipped seven tests that restated their own setup and proved nothing; the pattern to
avoid is a test whose assertion has no reader but itself.

- Version comparison: newer, older, equal, `v`-prefixed, malformed, prerelease, draft.
- Release-JSON parsing: well-formed, missing `tag_name`, empty body, non-JSON.
- `LastSeenVersion` suppression: the same release is not announced twice; a newer one is.
- **Default-off proof:** with default settings, no update code path constructs an HTTP request. This is the
  test a reviewer should be able to point at, and it must fail if the default flips.
- Purge: capture tables are emptied; `SettingsStore` and `Devices` are **not**; an open segment is closed
  first; the row count reported matches what was deleted.
- Storage: the reported size includes WAL and SHM when present.

Network access is never exercised in a test. The HTTP call sits behind a seam thin enough that the seam itself
needs no test, and the logic on either side of it is tested without it.

## 8. Done criteria

- A default install makes no network request. Verified by a test, not by inspection.
- Turning the toggle on and pressing Check now reports either a newer version with a working link, or that the
  app is current.
- Purge empties the recorded activity, leaves settings and device identity intact, shrinks the file on disk,
  and the UI shows zeroes immediately rather than after a restart.
- Storage usage matches what the filesystem reports for the three database files.
- About shows the real version, AGPL-3.0, and links that open.
- The README documents the update check's exact request and that it is off by default, and its badges point at
  this fork.
- Full suite green; `dotnet build --no-incremental` clean with zero warnings.
