<p align="center">
  <img src="assets/eViSTool.png" width="128" alt="eViSTool logo">
</p>

<h1 align="center">eViSTool</h1>

<p align="center">
  <b>Mod manager and dedicated server tool for Vintage Story</b><br>
  <i>Your world. Your rules.</i>
</p>

<p align="center">
  <a href="https://github.com/erneywhite/eViSTool/releases"><img src="https://img.shields.io/github/v/release/erneywhite/eViSTool?include_prereleases&label=release" alt="Latest release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/erneywhite/eViSTool" alt="License: GPL-3.0"></a>
  <a href="https://ko-fi.com/erneywhite"><img src="https://img.shields.io/badge/Ko--fi-support-ff5e5b?logo=ko-fi&logoColor=white" alt="Ko-fi"></a>
</p>

<p align="center">
  <b>English</b> · <a href="README.ru.md">Русский</a>
</p>

![My mods](docs/screenshots/mods.png)

eViSTool keeps your Vintage Story mods up to date, lets you browse and install mods from the ModDB,
and runs your dedicated server — console, players, backups and scheduled restarts — from one window.
It is portable: unzip it anywhere and run, nothing is installed into the system.

> **Status: alpha.** Everything described below works and is in daily use, but expect rough edges.
> Bug reports and ideas are very welcome in [Issues](https://github.com/erneywhite/eViSTool/issues).

## Features

### Mods
- See every installed mod with its version, side and status; check all of them for updates in one click.
- Update one mod or everything at once; pin a mod to its version or skip a specific release.
- Roll back to a previous version — replaced mod files are kept for that.
- Enable and disable mods exactly like the game's own mod manager does.
- Spot problems early: missing dependencies, duplicates, versions for another game branch.
- Share your setup as a modpack and import someone else's.

### Mod catalog
- The whole ModDB inside the app: search, tags, side, game version filters, sorting by trending, downloads and more.
- Screenshots, description and every release of a mod; install the right version with one button.

![Mod catalog](docs/screenshots/catalog.png)

### Profiles
- Several game setups side by side: client and server profiles, each with its own data folder.
- **Play** starts the game with the selected client profile — its own mods, settings and worlds.
- New client profiles and clones: share mods with the main game or keep a separate set, copy settings or start clean.
- Server profiles are cloned with the same world or a fresh one.

### Dedicated server
- Start, stop, restart and a live console. The server is owned by a small background agent,
  so it keeps running if you close the window, and the watchdog restarts it after a crash.
- Players online, uptime and memory at a glance.
- `serverconfig.json` editor with proper fields: general settings, world settings, roles and privileges with checkboxes,
  and everything else — unknown keys are preserved as they are.

![Server console](docs/screenshots/server.png)

- **Backups** on a schedule, with rotation, a chat announcement and one-click restore
  (the current world is saved aside first, so a restore can be undone).
- **Scheduled restarts** every N hours or at set times of day, with chat warnings
  10 and 5 minutes before and then every minute, and a fresh backup right before the restart.

| Configuration | Schedule |
|---|---|
| ![Configuration](docs/screenshots/config.png) | ![Schedule](docs/screenshots/schedule.png) |

### Coming next
- Remote management of a server on another machine with a one-string connection code (encrypted, no setup).
- Syncing the mods of a client profile with a server: missing mods, version differences, extras.

## Installation

1. Download `eViSTool-<version>-win-x64.zip` from [Releases](https://github.com/erneywhite/eViSTool/releases).
2. Unzip it into any folder and run `eViSTool.exe`.
3. eViSTool finds the game and your mods by itself. If it doesn't, point it to the game folder in **Settings**.

**Requirements:** Windows 10 or 11 (x64) and the .NET 10 Desktop Runtime — the game client needs the same runtime,
so it is already there if you play Vintage Story. On a machine with only a dedicated server,
Windows offers to download the runtime on the first start.

**Updates:** eViSTool checks GitHub releases on start and updates itself from **About** — a running server is not interrupted.

**Your data** (settings, backups of replaced mods, logs) lives in the `data` folder next to `eViSTool.exe`.
To remove eViSTool, delete its folder. eViSTool changes only mod folders, game and server settings files
(with a `.evistool.bak` copy of the previous state) and the folders of profiles it created.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build
dotnet test
pwsh build/publish.ps1   # release build and zip in dist/
```

Projects: `eViSTool.Core` — mods, ModDB, profiles, configs, backups; `eViSTool.Agent` — background agent that owns
the server process; `eViSTool.App` — WPF interface; `tests/eViSTool.Core.Tests` — tests.

## Support

eViSTool is free. If it saves you time, you can [buy me a coffee on Ko-fi](https://ko-fi.com/erneywhite) — it really helps keep it going.

## Credits and license

- [Rustique](https://github.com/Tekunogosu/Rustique) by Tekunogosu (MIT) — the ModDB and version logic is based on it.
- [ViSST Server Tool](https://mods.vintagestory.at/show/mod/17652) by THumbert — ideas for server management.

eViSTool is licensed under the [GNU General Public License v3.0](LICENSE). Third-party notices: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Vintage Story is a game by Anego Studios. eViSTool is an unofficial fan-made tool and is not affiliated with Anego Studios.

© 2026 Erney White
