# Daylane Titles & Detail (Sub-project 2) — Design

**Date:** 2026-09-09
**Roadmap:** [2026-09-08-daylane-hindsight-parity-roadmap.md](./2026-09-08-daylane-hindsight-parity-roadmap.md)
**Builds on:** [2026-09-08-daylane-foundation-design.md](./2026-09-08-daylane-foundation-design.md) (complete)
**Status:** Awaiting review

## 1. Goal

Make Daylane able to answer *"what was I actually doing inside that app?"* — today it only
knows which app was in the foreground.

1. Capture the foreground **window title**, opt-in and off by default.
2. Extract the **browser domain** (host only, never the full URL) for browser windows.
3. **Ignore rules** — exclude specific windows from counted time without deleting them.
4. An **app-detail view**: click an app, see what you did inside it, grouped by site for
   browsers.
5. **Search** over captured titles.

## 2. Non-goals

- **No AI. Ever.** Permanent product constraint.
- No screenshots, no OCR — that is sub-project 6.
- No categories or app groups — sub-project 3. This sub-project writes the titles those
  will later classify.
- No export, no date-range picker — sub-project 4.
- Windows only.

## 3. What the foundation already provides

Schema v2 shipped these columns and **nothing writes them yet**. This sub-project is what
makes them real.

| Column | Table | Purpose here |
|---|---|---|
| `WindowTitle` | `ActivitySegment` | The captured title |
| `UrlHost` | `ActivitySegment` | Browser domain, host only |
| `Excluded` | `ActivitySegment` | Set by ignore rules; excluded from counted time |
| `LocalDate` / `LocalHour` | both segment tables | Already written on capture |
| `DeviceId` / `UpdatedAt` | both segment tables | Already written on capture |

Also available: a JSON `SettingsStore` with `SettingsService.Update`, a live-reading
settings pattern (`IdleMonitor.Bind`), the `Timestamps.ToUtcText` helper, an append-only
migration framework, and a 124-test suite.

## 4. Privacy posture — the binding constraint

Daylane's README currently states, under Data and privacy:

> Not stored: keystrokes, window titles, screenshots, or mouse coordinates.

**This sub-project makes one of those four false**, and that is the whole reason it ships
opt-in. The rules:

- **`RecordWindowTitles` defaults to `false`.** A user who upgrades and changes nothing
  gets exactly today's behaviour: no titles, no hosts, no new data.
- **`RecordBrowserHost` defaults to `false` and is meaningless without titles** — it is
  gated behind title capture in the UI, because the host is derived during the same
  foreground sample.
- **Only the host is ever stored**, never the path or query. `https://mail.example.com/u/0/inbox?q=x`
  is stored as `mail.example.com`. This is not a truncation applied at display time; the
  rest is never persisted.
- **Privacy keywords suppress capture entirely.** A title or URL matching a user keyword
  means *no title and no host is written for that segment* — the row still exists and still
  counts, it just carries no detail. This mirrors Hindsight, where a page matching
  `privacyUrlKeywords` does not even get its domain recorded.
- **The README is rewritten in the same sub-project**, moving window titles and browser
  hosts out of the "not stored" list into an explicit opt-in description. Shipping the
  capture and fixing the docs later is not acceptable.

## 5. Two orthogonal rule systems — do not conflate them

Hindsight's own source warns that these are *"正交，容易混"* (orthogonal, easily confused),
and its comments are worth preserving because the distinction is genuinely slippery:

| | **Privacy keywords** | **Ignore rules** |
|---|---|---|
| Controls | Whether **detail is captured** | Whether **time is counted** |
| On a hit | No title, no host stored | Title stored, `Excluded = 1` |
| Row exists? | Yes | Yes |
| Counted in reports? | Yes | **No** |
| Reversible? | No — the detail was never written | **Yes** — clear the rule and recompute |

Ignore rules are the finer-grained sibling of a future `hidden` category: `hidden` will
exclude a whole app, an ignore rule can exclude *one kind of window* within an app. The
motivating case from Hindsight's comments: a terminal left running a download overnight —
you want the terminal counted, just not that window.

### Ignore rule shape, and one rule that must not be relaxed

```csharp
internal sealed record IgnoreRule(string ProcessName, string? TitleKeyword);
```

- `ProcessName` — exact match, case-insensitive, trimmed. **Required.**
- `TitleKeyword` — substring match, case-insensitive. `null` means the whole process.

**A rule may never be title-keyword-only.** Hindsight's comment explains why, and it is the
kind of reasoning worth copying verbatim into our source: a bare keyword like `Download`
would swallow matching windows across *every* application, and the swallowed time produces
no error and no warning — the totals simply stop adding up. Silent data loss is the
characteristic accident of this feature.

**`TitleKeyword = ""` is not the same as `null`.** An empty or all-whitespace keyword must
match *nothing*. `Contains("")` is always true, so treating empty as "match everything"
would let one slip in the UI silently exclude an entire application.

**Ignore rules are reversible.** Deleting a rule and recomputing `Excluded` across the table
restores the history. That recompute is a required part of this sub-project, not a nicety —
without it a mistaken rule is permanent in effect even after deletion.

Concretely: **the recompute runs whenever the rule list changes**, triggered from the
settings-changed path, and rewrites `Excluded` for every `ActivitySegment` row by
re-evaluating the current rules against each row's stored process name and title. It is a
full-table pass, which is acceptable because rule edits are rare and the table is indexed by
`LocalDate` rather than scanned per-row at read time. It must run inside one transaction so
a failure cannot leave half the table evaluated against old rules and half against new.

Note the dependency this creates: a row whose title was never captured (titles off, or a
privacy keyword hit) can only ever match a `null`-keyword whole-process rule. That is
correct — there is no title to test — but it means turning titles on does not retroactively
apply title-keyword rules to older rows.

## 6. Components

### 6.1 Title capture

`ForegroundTracker` already polls `GetForegroundWindow`. It gains `GetWindowText` beside the
existing process resolution, so a title costs no extra window enumeration.

`ForegroundApp` gains `WindowTitle` and `UrlHost`. **`SameIdentity` must not change.** It
currently compares process identity to decide whether a new segment starts; if it also
compared titles, every browser tab switch and every document rename would close one segment
and open another, multiplying row count for no analytical gain. Titles are recorded as
*attributes of the segment*, not as segment boundaries.

That decision has a visible consequence worth stating: a segment's stored title is the title
**at the moment the segment opened**. Retitling mid-segment is not tracked.

### 6.2 Browser host extraction

Ported from Hindsight's `capture/browser_url/windows.rs`, whose design notes are explicit
about the traps:

- Get the foreground `HWND`, then walk it with **UI Automation**, scanning `Edit` and
  `Document` controls; take the first value that parses as a URL. Try the value pattern,
  then legacy IAccessible value, then the element name.
- **The tree walk must specify an explicit depth.** Hindsight notes its matcher defaults to
  direct children only, and address bars sit deep in the subtree — without depth the search
  never finds anything.
- **Do not score candidates or try to identify "the real address bar".** Hindsight
  deliberately rejected that: a scoring threshold breaks when Chrome renames its omnibox
  class or when `chrome://` URLs carry no scheme, whereas an occasional false positive is
  cheap. Here the consequence of a wrong URL is a wrong host on one segment.
- **COM failures must not reach the process.** Hindsight wraps the call in `catch_unwind`
  for exactly this reason; our equivalent is a `try`/`catch` around the whole extraction
  returning null.

**Cost is the real risk.** A UIA tree walk takes tens of milliseconds, and `ForegroundTracker`
polls on a timer. Extraction therefore runs **only when the foreground app is a known
browser and the setting is on**, and only when the segment opens — not on every poll.

A "known browser" is a process-name match against a hardcoded `internal static readonly`
list — `chrome`, `msedge`, `firefox`, `brave`, `opera`, `vivaldi`, `arc`, `zen` — compared
case-insensitively without the `.exe`. A fixed list rather than a heuristic, because the
cost of guessing wrong is running an expensive UIA walk against every non-browser window.
Users cannot edit it; if a browser is missing, adding a string is a one-line change. If


this still proves too slow in practice, the fallback is to derive the host from the window
title, which most browsers include; the spec prefers UIA because titles are unreliable.

**Mechanism:** .NET has no bundled UIA client for a non-WPF app. The implementation should
use COM interop against `CUIAutomation` rather than taking a WPF dependency. If that proves
impractical, report it rather than pulling in WPF.

Only the host is kept: parse the URL, take `Host`, discard everything else.

### 6.3 App detail

Clicking an app opens a detail view listing what happened inside it:

- Per-title rows — title, total duration, session count — descending by duration.
- For browsers, **grouped by site**: `UrlHost` becomes the primary grouping, with titles
  nested beneath. Ported from Hindsight's `groupBySite.ts`.
- Excluded rows are shown but visibly marked, with their time not contributing to the total.
  Hiding them entirely would make the feature that excluded them undiscoverable.

### 6.4 Search

A search box over `WindowTitle` and `UrlHost` for the selected period, results grouped by
day and app, clicking through to the segment. Plain `LIKE` matching over the existing
`LocalDate` index — no FTS. FTS5 arrives in sub-project 6 for screen-memory text, and
introducing a second search implementation now would mean two to maintain.

### 6.5 Settings

Four additions to the existing Settings tab, in a new **Privacy** group:

| Setting | Default | Notes |
|---|---|---|
| `RecordWindowTitles` | `false` | Master switch for this sub-project |
| `RecordBrowserHost` | `false` | Disabled in the UI unless titles are on |
| `PrivacyKeywords` | empty | Title/URL substrings that suppress detail capture |
| `IgnoreRules` | empty | Process + optional title keyword; excluded from counted time |

The keyword and rule editors are list editors. Settings apply live, as everything else does.

## 7. Schema

**No new tables.** `WindowTitle`, `UrlHost` and `Excluded` already exist unwritten.

A **v3 migration** adds one index for title search:

```sql
CREATE INDEX IF NOT EXISTS IX_ActivitySegment_LocalDate_Title
    ON ActivitySegment (LocalDate, WindowTitle);
```

The `OpenAppSegment` composite index deferred by the foundation review stays deferred —
sub-project 4 will know its query shape and this one does not. Adding it here would be
guessing at an index's column order, which is the expensive kind of guess to undo.

`PrivacyKeywords` and `IgnoreRules` live in the JSON settings blob, not in tables — they are
user configuration, bounded in size, and the settings store already preserves unknown keys.

## 8. Error handling

| Failure | Behaviour |
|---|---|
| `GetWindowText` fails or returns empty | Store `NULL` title; the segment is otherwise normal |
| UI Automation throws, times out, or finds nothing | Store `NULL` host; never propagate |
| A URL fails to parse | Store `NULL` host rather than a partial value |
| A malformed ignore rule is loaded (empty process name) | Treat as inert — never let it match everything |
| Recompute of `Excluded` fails | Log and continue; rules still apply to new rows |

## 9. Testing

- **Ignore-rule matching is the highest-value target**, because its failure mode is silent.
  Cover: exact process match, case-insensitivity, `null` keyword excluding a whole process,
  `""` and whitespace keywords matching *nothing*, an empty process name being inert, and
  the reversibility recompute restoring previously-excluded rows.
- **Privacy keyword suppression** — a matching title stores neither title nor host, and the
  row still counts.
- **Host extraction** — URL parsing keeps only the host, across `https://`, `chrome://`,
  ports, subdomains, and junk input. The UIA walk itself needs a real browser window and is
  inspection-only; say so rather than mocking a fake tree.
- **`SameIdentity` unchanged** — a title change must not open a new segment. This is a
  regression test worth having, since the temptation to include titles in identity is real.
- **Search** — matches on title and host, respects the period, and excludes nothing that
  should match.
- **Default-off** — with `RecordWindowTitles` false, capture writes `NULL` title and host.
  This is the test that proves the privacy promise for anyone who upgrades and changes
  nothing.

## 10. Done criteria

- `dotnet test` green; `dotnet build` and `dotnet publish -c Release` clean.
- With the setting off, an upgraded install records no titles and no hosts.
- With it on, titles appear in app detail and search; browser rows group by host.
- An ignore rule removes time from totals; deleting it and recomputing restores that time.
- A privacy keyword suppresses detail while the row still counts.
- `README.md` updated: window titles and browser hosts moved out of "not stored" into an
  explicit opt-in description saying exactly what is kept.
