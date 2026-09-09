# Daylane → Hindsight Feature Parity: Roadmap

**Date:** 2026-09-08
**Status:** Approved (strategy + decomposition)

## Goal

Bring Daylane to feature parity with [Hindsight](https://github.com/Tomotsugu-dev/Hindsight),
implemented in pure C# / Avalonia, **excluding every AI feature**.

## Scope decisions

| Decision | Choice |
|---|---|
| AI features | **Excluded entirely.** No local LLM engine, no GGUF/llama.cpp, no embeddings, no AI daily/weekly reports, no AI chat, no vision models. There is no AI code path in Daylane. |
| Screen memory | Included. Text search uses `Windows.Media.Ocr` (the OS built-in), so no model downloads, no ONNX Runtime, no ML runtime of any kind. |
| Platforms | Windows only. Daylane's trackers are Win32 P/Invoke; no macOS port. |
| Privacy posture | Every new capture or sync capability ships **disabled by default** behind an explicit opt-in. `README.md` is rewritten to state exactly what each opt-in stores. |
| Cloud sync | Included (Google Drive), off by default. Not wire-compatible with Hindsight peers — separate app, separate payload format. |

## Size of the gap

|  | Daylane | Hindsight |
|---|---|---|
| Language | C# / Avalonia 12.1 | Rust + React/TS |
| Size | 5,627 LOC, 30 files | ~86,000 LOC (55k Rust, 31k TS), ~420 files |
| Tables | 3 (`DailyInput`, `ActivitySegment`, `OpenAppSegment`) | ~25 across two databases |
| Tests | none | Rust unit + e2e, vitest |

## Porting strategy

**Foundation-first**, adopting Hindsight's proven *shape* without its documented legacy.

Adopt:
- the `AppGroupMembers → AppGroups → Categories` classification chain
- last-write-wins `UpdatedAt` + soft-delete tombstones + a transactional outbox
- denormalized `LocalDate` / `LocalHour` for cheap report windowing
- the single-row JSON settings store

Reject (Hindsight's own docs mark these dead):
- `activities.category_id` — *"Deprecated — always `'other'`"*
- `activities.image_hash` — *"Unused legacy column… never written by any code path"*
- the `app_categories` mirror table — kept in Hindsight only for old sync peers

Keep from Daylane:
- portable single-file `daylane.db` next to the executable
- UTC timestamp storage (`StartUtc`). Hindsight stores local-offset RFC3339 and has to
  warn *"Compare with `datetime()`, not as plain strings, since offsets differ across
  devices"* — Daylane's choice avoids that class of bug. Local date/hour are
  denormalized alongside rather than replacing it.
- PascalCase column naming

Rationale for foundation-first: cloud sync retroactively changes every table (it needs
`UpdatedAt` LWW, tombstones, `DeviceId`/`RemoteId`, and an outbox row written in the same
transaction as every business write). Landing that schema before the features that write
to it means each feature is written once.

## Sub-projects

Each gets its own spec → plan → implementation → commit cycle.

| # | Sub-project | Delivers | Depends on |
|---|---|---|---|
| 1 | **Foundation** | Schema v2, settings store + settings UI, dark mode + theme tokenization, test project | — |
| 2 | **Titles & detail** | Window-title capture (opt-in), ignore/privacy rules, browser domain extraction, app-detail drawer, title search | 1 |
| 3 | **Taxonomy** | App groups (merge/unmerge), categories, super-categories, editor UI, category rollups | 1, 2 |
| 4 | **Reporting** | Date-range picker, pie / ranked-list / insight tiles, xlsx + CSV export, empty & error states, **work hours** | 3 |
| 5 | **i18n** | 6 locales, runtime locale switching | 1 |
| 6 | **Screen memory** | Capture service, L1 stillness gate, frame store, sessions, `Windows.Media.Ocr` text layer, FTS5 search, retention | 1, 2 |
| 7 | **Sync** | Device identity, outbox drain, Google Drive OAuth + AES-GCM, push/pull engine, multi-device UI | 1, 3 |
| 8 | **Updates, About & data management** | Update check (opt-in, off by default), About panel, purge data, storage-usage display | 1 |

### Ordering notes

- **Dark mode is in #1** because it needs a settings home to be toggled from, and because
  tokenizing the hardcoded colors touches every style block — cheaper before features add more.
- **Sync is last** because its failure modes are distributed, and it wants a stable taxonomy.
- **#6 must port the L1 frame-difference stillness gate as specified.** Hindsight's design
  doc explicitly warns off the obvious shortcut: *"哈希族已证伪,勿回退"* — the hash family
  (dHash and friends) is disproven, because grid-mean/gradient summaries go structurally
  blind to same-density text replacement at any threshold. dHash is retained there only as
  a storage fingerprint, never as the gate.

## Audit — 2026-09-09

The original decomposition was built from Hindsight's README and file tree. A later pass
through its five Settings tabs (General, Appearance, Privacy, Data, About) found four
features the roadmap had missed entirely. Everything else mapped to an existing
sub-project: Privacy → #2, Export → #4, Remove device → #7, Appearance/capture/idle → #1,
OCR and screenshots → #6.

| Found | Hindsight has | Placed in |
|---|---|---|
| Update checking | Check now, auto-check toggle, interval (daily/weekly/monthly/on startup), version display, install flow | **#8** |
| Data management | Purge database, purge screenshots, storage-usage display, data path | **#8** |
| About panel | Version, author, license, repo and feedback links | **#8** |
| Work hours | Define working hours; shades period charts and the status footer | **#4** |

Two things were checked and deliberately excluded rather than missed:

- **`BackfillBanner`** is OCR-engine machinery — model download progress, digest runs,
  resident-mode confirmation. It belongs to #6 and is largely moot there, since this port
  uses `Windows.Media.Ocr` rather than a downloaded ONNX model.
- **`tauri-plugin-notification`** is registered in `lib.rs` but has no Rust caller; the
  usage appears to be AI-summary completion, which is permanently out of scope.

### Update checking (#8) — scope decision

**Opt-in, off by default.** A default install contacts nothing, matching how every other
capability in this port ships and preserving the README's "No accounts, no cloud" promise
for anyone who does not turn it on. When enabled it checks GitHub Releases for a newer tag
and links to the download; the README must state exactly what is sent and when. Daylane has
no updater today and .NET offers no equivalent to `tauri-plugin-updater`, so this is a
build rather than a port.

## Carried forward from sub-project 2 (2026-09-09)

Sub-project 2 shipped with two architectural findings deliberately parked rather than
fixed in its final wave, plus a short list of smaller ones. They are recorded here
because the execution ledger they were found in is scratch and does not survive the
branch.

### Parked, architectural — schedule before sub-project 7 (sync)

- **`RecomputeExcluded` holds `_dbWriteLock` for its whole pass.** The full-table
  re-evaluation was deliberately moved off the UI thread so a rule edit could not freeze
  the window, but every read — `GetDaySnapshot`, `GetSegmentsForLocalRange`,
  `GetTitleUsage`, `SearchTitles` — takes that same lock, so on a large database the
  freeze happens anyway. The move was right; the premise that it was sufficient was not.
  Fixing it properly means deciding whether reads should take that lock at all, since
  WAL already permits concurrent readers. That touches every read path in the store,
  which is why it was not folded into a fix wave.

- **A sample-then-act gap in `TrackingService.SwitchSegment`.** Keeping the unbounded
  UI Automation walk outside `_segmentStateLock` was necessary — holding the lock across
  a hung browser renderer froze the window. But before that change the whole method body
  was inside the lock, so concurrent `Changed` handlers serialised with no gap between
  sampling the foreground app and acting on it. There is now a 46ms-to-unbounded gap, and
  `System.Threading.Timer` does not serialise its callbacks. Worst case: an older switch
  commits after a newer one, leaving two rows with mis-ordered `StartUtc` and a stale
  current-app in the header until the next switch. Not data loss. The fix shape is a
  switch sequence number checked inside the lock, or an identity re-check against the
  live foreground app after the walk; it also needs a way to reproduce a genuinely slow
  walk.

### Smaller, opportunistic

- Two of the five totals corrected in the final wave (`UpdateFocusSummary`'s
  longest-focus/session-count, and the `% of tracked` denominator) filter excluded
  segments by hand rather than through the shared `SegmentWindows.Counted` enumerator,
  so they are the exception to the by-construction guarantee — and both live in a view
  model that cannot be constructed in a test, so neither is covered.
- `AggregateOpenApps` keeps its own clip/clamp preamble, a fourth copy of the shape the
  final wave consolidated. Defensible (different table, needs `currentlyOpen`) but the
  "one shared helper" claim is four of five.
- `SettingsService.Update` now reports a failed persist, but every caller discards the
  `bool`, so a failed settings write is invisible to the user. History is no longer
  recomputed against unpersisted rules, which was the correctness half; surfacing it is
  the remaining half.
- An excluded *idle* segment still increments the Away count while dropping out of the
  Idle figure, so the two disagree. Reachable only by writing an ignore rule for the
  pseudo-process `Idle`.
- `Daylane.Tests` still emits three nullable warnings that predate sub-project 2. The
  warning-clean gate covers `Daylane.csproj` only.

### Never verified by running the app

No UI on this branch was ever rendered — Daylane's single-instance mutex means launching
it during development pops the user's live window to the foreground, so it stayed closed
throughout. Compiled bindings and the colour-literal gate are the only evidence behind
the Privacy panel, the app-detail drawer and the search popup. The first things worth
clicking are listed at the end of the sub-project 2 branch review; the highest-value
check is a browser with **Record browser site** on, because a wrong vtable ordering in
the hand-written UI Automation interop is swallowed by design and reports as "no host"
rather than as an error.
