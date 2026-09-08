# Daylane

[![License](https://img.shields.io/github/license/mirbyte/Daylane?color=58C090)](https://raw.githubusercontent.com/mirbyte/Daylane/main/LICENSE)
![Size](https://img.shields.io/github/repo-size/mirbyte/Daylane?label=size&color=2F9E6B)
[![Download Count](https://img.shields.io/github/downloads/mirbyte/Daylane/total?color=58C090)](https://github.com/mirbyte/Daylane/releases/latest)
[![Latest Release](https://img.shields.io/github/release/mirbyte/Daylane.svg?color=58C090)](https://github.com/mirbyte/Daylane/releases/latest)

Windows activity tracker. Records which apps you used, when you were away, and how much you typed or clicked. Everything stays on disk next to the exe. No accounts, no cloud.

Windows 10/11 x64. Binaries: [Releases](../../releases). Unzip and run `Daylane.exe`.

## Features

- Day timeline: foreground apps, Active/Away, input intensity
- Week and month insights. Month can take several seconds to open, longer on slower machines.
- Open-app time (visible windows, not only focus)
- Light and dark theme, or follow Windows
- Settings in-app; changes apply immediately
- Tray icon; optional Start with Windows

## Data and privacy

`daylane.db` is created beside the executable (portable). Stored: process name, exe path, time ranges, key/click **counts**. Not stored: keystrokes, window titles, screenshots, or mouse coordinates.

Menu → Open data folder.

On first launch after upgrading, `daylane.db` is migrated and a backup of
the previous version is written beside it as `daylane.db.bak.v1`. Delete
it once you are satisfied the upgrade went cleanly.

## Settings

Open the **Settings** tab. Changes apply immediately — no restart.

- **Appearance** — System, Light, or Dark; System follows Windows
- **Idle threshold (minutes)** — minutes without keyboard or mouse input before a span is marked Away (1–240)
- **Keep history for (days)** — delete records older than N days; defaults to `0`, which keeps everything until you opt in
- **Startup** — Start with Windows, Show window on startup, Minimize to tray

Settings live in `daylane.db`. The old `config.ini` is read once on
first launch after upgrading, to carry your `threshold_minutes` across;
after that the file is ignored and can be deleted.

## Build

[.NET 10 SDK](https://dotnet.microsoft.com/download). Release publish is a self-contained `win-x64` single file:

```powershell
dotnet publish -c Release
```

---

<img width="3839" height="2017" alt="maintab" src="https://github.com/user-attachments/assets/c82a0f46-52f0-4cae-8244-5762a1746e2c" />

