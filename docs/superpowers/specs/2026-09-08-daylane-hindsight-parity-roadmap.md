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
| 4 | **Reporting** | Date-range picker, pie / ranked-list / insight tiles, xlsx + CSV export, empty & error states | 3 |
| 5 | **i18n** | 6 locales, runtime locale switching | 1 |
| 6 | **Screen memory** | Capture service, L1 stillness gate, frame store, sessions, `Windows.Media.Ocr` text layer, FTS5 search, retention | 1, 2 |
| 7 | **Sync** | Device identity, outbox drain, Google Drive OAuth + AES-GCM, push/pull engine, multi-device UI | 1, 3 |

### Ordering notes

- **Dark mode is in #1** because it needs a settings home to be toggled from, and because
  tokenizing the hardcoded colors touches every style block — cheaper before features add more.
- **Sync is last** because its failure modes are distributed, and it wants a stable taxonomy.
- **#6 must port the L1 frame-difference stillness gate as specified.** Hindsight's design
  doc explicitly warns off the obvious shortcut: *"哈希族已证伪,勿回退"* — the hash family
  (dHash and friends) is disproven, because grid-mean/gradient summaries go structurally
  blind to same-density text replacement at any threshold. dHash is retained there only as
  a storage fingerprint, never as the gate.
