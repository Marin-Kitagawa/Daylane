# Daylane → Hindsight parity: progress

Live tracker for the whole port. Roadmap and per-sub-project specs live beside this file.
**Scope, fixed at the outset: no AI features, ever. Windows only. Every new capture or sync
capability ships off by default.**

Updated 2026-09-10.

## Sub-projects

| # | Sub-project | Status |
|---|---|---|
| 1 | Foundation — schema v2, settings store + UI, dark mode, test project | ✅ **Done**, merged to main |
| 2 | Titles & detail — title capture, ignore/privacy rules, browser host, detail drawer, search | ✅ **Done**, merged to main |
| 8 | Updates, About & data management | 🔵 **In progress** — 7 of 12 tasks |
| 3 | Taxonomy — app groups, categories, super-categories, editor, rollups | ⬜ Not started |
| 4 | Reporting — date-range picker, pie/ranked/insight tiles, xlsx + CSV export, work hours | ⬜ Not started (needs 3) |
| 5 | i18n — 6 locales, runtime switching | ⬜ Not started |
| 6 | Screen memory — capture, L1 stillness gate, frame store, `Windows.Media.Ocr`, FTS5, retention | ⬜ Not started (needs 2) |
| 7 | Sync — device identity, outbox drain, Google Drive + AES-GCM, push/pull, multi-device UI | ⬜ Not started (needs 3) |

## Sub-project 8 — current

Branch `worktree-daylane-updates`. Baseline was 229 tests; now 290.

| Task | What | Status |
|---|---|---|
| 1 | Settings keys (`CheckForUpdates` off by default, `LastSeenVersion`) | ✅ Done, 1 finding parked |
| 2 | Version comparison — tags as versions, not strings | ✅ Done, clean |
| 3 | Release JSON parsing — every unusable input returns null | ✅ Done, clean |
| 4 | The update checker + **the default-off proof** | ✅ Done, clean |
| 5 | App info — version, author, AGPL-3.0, links | ✅ Done after 1 fix round |
| 6 | The single network call (deliberately untested) | ✅ Done, clean |
| 7 | Purge recorded activity | ✅ Done after 1 fix round |
| 8 | Storage usage — size incl. WAL, row count, path | 🔵 In review |
| 9 | Drive purge + storage through `TrackingService` | ⬜ Next |
| 10 | View-model surface for all three panels | ⬜ |
| 11 | The Updates, Data and About settings panels | ⬜ |
| 12 | README — document the exact request, repoint fork badges | ⬜ |

Then: whole-branch review → one fix wave → merge.

## Carried forward from sub-project 2

Recorded in the roadmap; not yet scheduled.

- **`RecomputeExcluded` holds `_dbWriteLock` for its whole pass**, so moving it off the UI thread
  did not prevent the freeze it targeted. Fixing it properly means deciding whether reads should
  take that lock at all, which touches every read path in the store. Do before #7.
- **A sample-then-act gap in `SwitchSegment`**, opened by moving the browser walk outside the lock.
  Worst case: two rows with mis-ordered `StartUtc` and a stale current-app until the next switch.
  Needs a switch sequence number and a way to reproduce a slow walk.
- Two of the five corrected totals filter excluded segments by hand rather than through the shared
  `SegmentWindows.Counted`, and neither is testable (they live in a view model that cannot be
  constructed).
- `AggregateOpenApps` keeps a fourth copy of the clip/clamp preamble.
- `SettingsService.Update` now reports a failed persist, but every caller discards the `bool`.
- An excluded *idle* segment still increments the Away count while dropping out of the Idle figure.
- `Daylane.Tests` emits three nullable warnings that predate sub-project 2.

## Never verified by running the app

**No UI from sub-project 2 or 8 has ever been rendered.** Daylane's single-instance mutex means
launching it during development pops the user's live window to the foreground, so it stayed closed
throughout. Compiled bindings and the colour-literal gate are the only evidence behind the Privacy
panel, the app-detail drawer, the search popup, and everything sub-project 8 adds.

Highest-value manual checks, in order:

1. Day view → click an app in the Apps list. Confirm real titles and sane durations.
2. Settings → Privacy: toggle titles on, confirm the browser-site switch enables.
3. A browser with **Record browser site** on. **A missing host header here means reading
   `BrowserHost.cs`, not "no browser open"** — the UI Automation interop's total catch reports a
   wrong vtable ordering identically to no result.
4. Add an ignore rule for an app with time today; confirm its row drops out of the Apps list and
   the timeline block reads "Excluded from totals".
5. Dark mode, on every new panel.
