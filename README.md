# Daylane

[![License](https://img.shields.io/github/license/Marin-Kitagawa/Daylane?color=58C090)](https://raw.githubusercontent.com/Marin-Kitagawa/Daylane/main/LICENSE)
![Size](https://img.shields.io/github/repo-size/Marin-Kitagawa/Daylane?label=size&color=2F9E6B)
[![Download Count](https://img.shields.io/github/downloads/Marin-Kitagawa/Daylane/total?color=58C090)](https://github.com/Marin-Kitagawa/Daylane/releases/latest)
[![Latest Release](https://img.shields.io/github/release/Marin-Kitagawa/Daylane.svg?color=58C090)](https://github.com/Marin-Kitagawa/Daylane/releases/latest)

Windows activity tracker. Records which apps you used, when you were away, and how much you typed or clicked. Everything stays on disk next to the exe. No accounts, no cloud — the optional update check is the one exception, off by default; see [Updates](#updates).

Windows 10/11 x64. Binaries: [Releases](../../releases). Unzip and run `Daylane.exe`.

## Features

- Day timeline: foreground apps, Active/Away, input intensity
- Week and month insights. Month can take several seconds to open, longer on slower machines.
- Open-app time (visible windows, not only focus)
- App detail: select an app to see its time broken down by window title, or by site for browsers
- Search: find captured window titles and browser hosts for the day
- Light and dark theme, or follow Windows
- Settings in-app; changes apply immediately
- Tray icon; optional Start with Windows

## Data and privacy

`daylane.db` is created beside the executable (portable). Always stored: process name, exe path, time ranges, key/click **counts**. Never stored: keystrokes, screenshots, or mouse coordinates.

Window titles and browser sites are both **off by default** — an upgraded install records nothing new until you turn them on yourself, in Settings → Privacy. Turning on **Record window titles** stores each window's title alongside its app. Turning on **Record browser site** (which requires window titles to be on) stores only the site's **host**, never the full address — no path, query string, or fragment.

**Private keywords** suppress capture for any window whose title or site matches one: nothing is recorded for that window, but the time still counts toward your totals. **Ignored windows** work the other way around: the title/site is still recorded, but the time stops counting toward totals — remove the rule and that time counts again.

Menu → Open data folder.

On first launch after upgrading, `daylane.db` is migrated and a backup of
the previous version is written beside it as `daylane.db.bak.vN`, where `N`
is the schema version you upgraded from (for example `daylane.db.bak.v2`).
Delete it once you are satisfied the upgrade went cleanly.

## Settings

Open the **Settings** tab. Changes apply immediately — no restart.

- **Appearance** — System, Light, or Dark; System follows Windows
- **Track activity** — pause to stop recording new activity; nothing already recorded is deleted, the segment in progress is saved (not discarded) at the moment you pause, and pausing is not the same as quitting
- **Idle threshold (minutes)** — minutes without keyboard or mouse input before a span is marked Away (1–240)
- **Keep history for (days)** — delete records older than N days; defaults to `0`, which keeps everything until you opt in
- **Startup** — Start with Windows, Show window on startup, Minimize to tray
- **Privacy** — Record window titles, Record browser site (needs window titles on), Private keywords, Ignored windows — see [Data and privacy](#data-and-privacy)
- **Updates** — Check for updates (off by default), Check now — see [Updates](#updates)
- **Data** — Delete recorded activity: permanently deletes all recorded activity on this computer. Your settings are kept. The database file shrinks afterward, because the purge vacuums it.
- **About** — Version, license, and links to the repository, issue tracker, license text, and Hindsight, the project Daylane's feature set is ported from

Settings live in `daylane.db`. The old `config.ini` is read once on
first launch after upgrading, to carry your `threshold_minutes` across;
after that the file is ignored and can be deleted.

## Updates

**Check for updates** is **off by default**. A default install contacts nothing.

When turned on, Daylane sends one unauthenticated HTTPS `GET` to
`https://api.github.com/repos/Marin-Kitagawa/Daylane/releases/latest`, once per launch. The
request carries a `User-Agent` of `Daylane/<version>` and an `Accept` header, and nothing else —
no account, no token, no identifier, no telemetry. GitHub necessarily observes the requesting IP,
as it would for any HTTPS request, and that version string.

**Check now** performs a single check immediately, even while the switch is off — pressing a
button is an explicit act, distinct from the automatic once-per-launch check the switch controls.

Daylane never downloads or installs an update. If a newer release is found, it shows a link that
opens the release page in your browser; nothing is fetched beyond the version check itself.

## Build

[.NET 10 SDK](https://dotnet.microsoft.com/download). Release publish is a self-contained `win-x64` single file:

```powershell
dotnet publish -c Release
```

---

<img width="3839" height="2017" alt="maintab" src="https://github.com/user-attachments/assets/c82a0f46-52f0-4cae-8244-5762a1746e2c" />

