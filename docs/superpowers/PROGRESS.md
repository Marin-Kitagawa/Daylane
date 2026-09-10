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
| 8 | Updates, About & data management | ✅ **Done**, awaiting merge |
| 3 | Taxonomy — app groups, categories, super-categories, editor, rollups | ⬜ Not started |
| 4 | Reporting — date-range picker, pie/ranked/insight tiles, xlsx + CSV export, work hours | ⬜ Not started (needs 3) |
| 5 | i18n — 6 locales, runtime switching | ⬜ Not started |
| 6 | Screen memory — capture, L1 stillness gate, frame store, `Windows.Media.Ocr`, FTS5, retention | ⬜ Not started (needs 2) |
| 7 | Sync — device identity, outbox drain, Google Drive + AES-GCM, push/pull, multi-device UI | ⬜ Not started (needs 3) |

## Sub-project 8 — complete, awaiting merge

Branch `worktree-daylane-updates`. Baseline was 229 tests; now **294**, build clean at zero warnings under
`--no-incremental`. All 12 tasks done; whole-branch review clean after one fix wave.

That fix wave closed two Critical defects, and both are worth remembering because neither was a task's fault —
each task implemented its brief correctly and **the brief was wrong**:

- **`VACUUM` reclaimed nothing.** Daylane runs in WAL mode, where `VACUUM` rebuilds the database *into the WAL*.
  Without a checkpoint the main file is never truncated, so the purge freed no byte a user could see — measured
  at 1,245,184 bytes before a purge and **1,529,304 after**: the footprint *grew*. Fixed with
  `PRAGMA wal_checkpoint(TRUNCATE)`, and now 1,245,184 → 188,416, pinned by a test that fails without it.
- **A purge stopped recording** until the user next changed apps. Both trackers are edge-triggered; the purge
  cleared the segment state without re-priming their caches, so they saw no change and opened nothing.
  `ForegroundTracker.Stop()`'s own comment documents this hazard — the pause path clears the caches for exactly
  this reason, and the purge did not.

What it delivers: an opt-in update check (off by default, once per launch, links out rather than installing), a
data panel (purge, storage usage incl. WAL, data path) and an About panel.

## Carried forward — not yet scheduled

Both lists live in full in `docs/superpowers/specs/2026-09-08-daylane-hindsight-parity-roadmap.md`.

**From sub-project 2**, two architectural items to do before #7: `RecomputeExcluded` holds `_dbWriteLock` for its
whole pass (so moving it off the UI thread did not prevent the freeze it targeted), and a sample-then-act gap in
`SwitchSegment` opened by moving the browser walk outside the lock. Plus five smaller ones.

**From sub-project 8**, five items, the notable one being that a *busy* WAL checkpoint reports itself in the
pragma's result row rather than as an exception — so the silent-no-reclaim failure the checkpoint was added to
eliminate is still reachable and would log nothing.

## Never verified by running the app

**No UI from sub-project 2 or 8 has ever been rendered.** Daylane's single-instance mutex means launching it
during development pops the user's live window to the foreground, so it stayed closed throughout. Compiled
bindings and the colour-literal gate are the only evidence behind the Privacy panel, the app-detail drawer, the
search popup, and everything sub-project 8 adds.

One gate is weaker than it looks: **a green Avalonia build is not evidence a binding works.** Compiled bindings
check that the path resolves, not that the source and target types are convertible — a `string` bound to
`IsVisible` fails only at runtime, leaving the element permanently visible. That shipped once on this branch and
was caught by a reviewer building a headless harness, not by the build.

Highest-value manual checks, in order:

1. **Fill the database, then purge, and watch the size number.** Settings → note the size → Delete recorded
   activity. It should drop sharply. This is the Critical that shipped broken and was fixed.
2. **Purge, then keep working in the same apps for ten minutes without opening or closing a window.** The Day
   view should still be recording. This is the second Critical.
3. Day view → click an app in the Apps list. Confirm real titles and sane durations.
4. Settings → Privacy: toggle titles on, confirm the browser-site switch enables.
5. A browser with **Record browser site** on. **A missing host header here means reading `BrowserHost.cs`, not
   "no browser open"** — the UI Automation interop's total catch reports a wrong vtable ordering identically to
   no result.
6. Add an ignore rule for an app with time today; confirm its row drops out of the Apps list and the timeline
   block reads "Excluded from totals".
7. The three new panels' conditional bits, since no static gate covers them: the confirmation panel appears and
   collapses, the result line appears only after a purge, the Download button only when an update exists.
8. Dark mode, on every new panel.
