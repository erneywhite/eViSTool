# eViSTool configuration files on the server machine

**English** · [Русский](config.ru.md)

Everything the eViSTool agent keeps about the server is in the `data` folder next to `eViSTool.Agent`. If that folder
isn't writable, eViSTool falls back to `~/.local/share/eViSTool` on Linux or `%LOCALAPPDATA%\eViSTool` on Windows. The
`setup` command doesn't allow that and asks you to give the eViSTool folder to the user the agent runs as.

The paths below are relative to `data`. `<profile>` in file names is the profile id: `server` for an agent without a
window (or whatever `Profile` in `agent.json` says), and the profiles' own ids for the eViSTool window.

The eViSTool window and the agent write almost all of these files, but you can edit them by hand too. A few rules:

- The files are JSON in UTF-8. JSON has no comments: put your note in a separate field, such as `"Note": "…"`.
  `setup` keeps such fields in `agent.json`.
- Edit as the same user the agent runs as: `sudo -u vintagestory nano data/agent.json`. A file saved as root can't be
  overwritten by the agent later.
- A mistake in `agent.json` makes the agent report it and refuse to start. Any other broken file is quietly replaced by
  defaults: with a typo in `automation.json`, scheduled backups simply stop. After editing, check the eViSTool window or
  the server console.
- The agent reads `agent.json` only at startup (restart the agent or the service after editing). The schedule,
  announcements and remote access are re-read on the fly, about every five seconds. Notification settings and the
  statistics switch are checked before every send and every record.
- The window saves a file as a whole: if a tab with the same settings is open in the window, saving there writes what
  the tab shows, and your manual edit is lost.

## data/agent.json — the agent without a window

The `setup` command writes this file, and the agent reads it when started without a window (always the case on Linux).
The Windows window passes the folders to the agent itself, and then `agent.json` isn't read.

```json
{
  "GameDir": "/home/vintagestory/server",
  "DataDir": "/var/vintagestory/data",
  "Profile": "server",
  "StartServer": true,
  "ServerName": "",
  "ServerArgs": [],
  "Language": ""
}
```

| Field | What it is | If empty or missing |
|---|---|---|
| `GameDir` | The server's game folder, the one with `VintagestoryServer.dll` | the agent looks for it: next to itself, in neighbouring folders, through `server.sh`, in `/home/vintagestory/server` |
| `DataDir` | The server data folder (its `--dataPath`): world, mods, `serverconfig.json` | from this game's `server.sh`, otherwise the game's default (`~/.config/VintagestoryData`) |
| `Profile` | The agent's profile. All file names in `agents/` depend on it | `server` |
| `StartServer` | Start the server together with the agent | `true` |
| `ServerName` | Server name for backups (`<name>-2026-10-09_17-51-32.vcdbs`) and for notification titles, unless the window sets its own | `ServerName` from `serverconfig.json`; without that, backups are named `world-…` |
| `ServerArgs` | Extra server arguments, one per list item | none |
| `Language` | Language of the agent's messages and of chat announcements to players: `en` or `ru` | the system language; for a systemd service that's usually English |

The agent takes each value from its command-line option, and if there is none, from `agent.json`. Folders it can't
find in either place it looks for itself. The options are `--game`, `--data`, `--profile`, `--arg`, `--start` and
`--no-start`, `--lang`. For example, `./eViSTool.Agent --data /var/vintagestory/test` takes the game from `agent.json`
but different data.

Better write full paths. A relative path counts from the folder `agent.json` is in, and `~/` means the home folder of
the user the agent runs as.

In backup names, spaces and characters that can't appear in file names turn into `_`: "Survival Island" becomes
`Survival_Island-…`. Rotation deletes only backups with its own name. If you change the name, old backups stay in the
list, but the schedule won't delete them any more.

You can run `setup` again. It updates the folders (and whatever you pass in options: `--profile`, `--name`, `--arg`,
`--start`, `--no-start`) and leaves the rest of the file as it was, including fields eViSTool doesn't know. If the file
is broken, `setup` leaves it alone and says where the error is.

## agents/&lt;profile&gt;.automation.json — the schedule

The "Schedule" tab in the window. The agent makes world backups, restarts and mod updates on restart by itself; the
window doesn't need to be open for that.

| Field | What it is | Default |
|---|---|---|
| `BackupEnabled` | Make scheduled world backups while the server is running | `false` |
| `BackupIntervalHours` | Every how many hours (fractional; no more often than every 5 minutes, no less often than every 30 days) | `1` |
| `BackupKeep` | How many of its latest backups to keep, `0` means never delete | `7` |
| `BackupOnlyWhenPlayed` | Skip a backup if nobody has joined since the last one | `true` |
| `BackupAnnounce` | Tell players in chat that a backup is done | `true` |
| `BackupDir` | Where backups go (a full path, a network folder works too). The server still writes the backup into its own `Backups`, and the agent then moves it here | `null`, meaning `Backups` in the server data |
| `RestartMode` | `"Off"`, `"Interval"` (every N hours of uptime) or `"Daily"` (at set times) | `"Off"` |
| `RestartIntervalHours` | For `Interval`: restart after this many hours of uptime (at least 5 minutes) | `12` |
| `RestartTimes` | For `Daily`: times of day, `["05:00", "17:30"]` | `["05:00"]` |
| `RestartWarnMinutes` | How many minutes ahead to warn players (1 to 180) | `[10, 5, 4, 3, 2, 1]` |
| `RestartBackup` | Make a world backup before a scheduled restart | `true` |
| `RestartUpdateMods` | While the server is down for the restart, install released mod updates | `false` |
| `UpdatePinned` | Pinned mods, not to be updated: `{"modid": "version"}` | empty |
| `UpdateBlocked` | Skipped versions, not to be installed: `{"modid": ["1.2.3"]}` | empty |
| `UpdateAllowUnstable` | Offer pre-releases | `false` |

The window rewrites `UpdatePinned`, `UpdateBlocked` and `UpdateAllowUnstable` from its own mod settings when it saves
the schedule. All times are the server machine's local time, and on Ubuntu that's UTC by default.

## agents/&lt;profile&gt;.remote.json — remote access

With this file the agent accepts connections from an eViSTool window on another computer: over TLS, on its own port,
with its own key. Turn it on and off with `eViSTool.Agent remote …` or in the window.

| Field | What it is |
|---|---|
| `Enabled` | Accept connections from the network |
| `Port` | TCP port. Picked at random from 20000–60000 the first time and never changed. Open it in the firewall |
| `Key` | The remote connection key |
| `Host` | The address that goes into the connection code: a local IP, the public IP or a domain. If empty, the machine's first address is used |

You can change `Enabled`, `Host` and `Port` by hand; the agent picks the change up within a few seconds. The address
and port are part of the connection code, so after changing them get the code again (`remote code`) and paste it into
the window once more.

Don't edit `Key` by hand: the agent won't accept a key shorter than 32 characters, and remote access won't turn on.
`remote new-key` gives a new key; after it everyone connected from outside is dropped and needs the new code. The
agent's certificate sits next to it in `agents/<profile>.remote.pfx`. Leave it alone too: if the file is deleted, the
agent creates a new certificate with a different fingerprint, and windows that know the old code can't connect.

The key is stored in plain text in this file. Whoever reads it can control the server, so the `data` folder must be
accessible only to the agent's user (`setup` creates it with `700` permissions).

## agents/&lt;profile&gt;.notify.json — notifications

The "Notifications" tab: which event goes to which channels (Telegram, Discord, ntfy, webhook). Channel secrets (bot
token, webhook address, ntfy topic) are kept in the `SecretProtected` field, encrypted with the key of the user the
agent runs as. Set them from the window: it passes secrets to the agent over the secure connection, and the agent
encrypts them on its side. The agent can't decrypt a `SecretProtected` string copied from another machine or another
user.

You can edit the rest by hand:

- `ServerName` — the server name in titles ("Survival: server crashed"). The window writes the profile name here. If
  the field is empty, an agent without a window takes `ServerName` from `agent.json` or `serverconfig.json`.
- `Routes` — an event and a list of channel ids: `{"ServerCrashed": ["<id>"]}`. Events: `ServerCrashed`,
  `StartFailed`, `BackupFailed`, `LowDisk`, `Overloaded`, `ModsUpdated`, `ModUpdates`, `ServerStarted`,
  `ServerStopped`, `RestartSoon`, `PlayerJoined`, `PlayerLeft`.
- `ChatChannelId` — the id of the Discord channel the game's general chat is forwarded to (`null` means don't forward),
  and `ChatJoins` — whether to post there who joined and left.
- In `Channels`, every channel has `Id`, `Name`, `Kind` (`0` Telegram, `1` Discord, `2` ntfy, `3` webhook) and the
  fields of its kind: `ChatId` and `TopicId` for Telegram, `Url` for ntfy (your own server, empty means ntfy.sh),
  `Template` for a webhook.

## agents/&lt;profile&gt;.announcements.json — announcements

The "Announcements" tab: scheduled messages to players in chat.

```json
{
  "OnlyWithPlayers": true,
  "Items": [
    { "Text": "Server map: map.example.org", "IntervalMinutes": 60, "Enabled": true }
  ]
}
```

`OnlyWithPlayers` — speak only when someone is on the server (`true` by default). Each announcement has its own interval
in minutes, from 1 to 1440 (30 by default); two announcements don't go out in the same minute, the second one waits.

## agents/&lt;profile&gt;.stats/ — statistics

Once a minute the agent records how many players are on the server, how much memory it uses and how busy the CPU is,
plus joins, leaves, starts and crashes. One file per day (`2026-10-09.log`), kept for 30 days.

Collection is switched on by an empty `enabled` file in this folder, separately from the schedule. The window sets it on
the "Statistics" tab; by hand it goes like this:

```sh
sudo -u vintagestory mkdir -p data/agents/server.stats
sudo -u vintagestory touch data/agents/server.stats/enabled   # turn on
sudo -u vintagestory rm data/agents/server.stats/enabled      # turn off
```

## The secrets key: secret.key

On Linux, notification channel secrets (tokens, webhook addresses, ntfy topics) are encrypted with a per-user key. It
lives in `~/.local/share/eViSTool/secret.key` (or `$XDG_DATA_HOME/eViSTool/secret.key`) of the user the agent runs as,
with `600` permissions. If the user has no home folder or it isn't writable, the key goes to `data/secret.key`.

Without this file the saved secrets can't be decrypted. If you move the server to another machine, move the key too,
or you'll have to set the notification channels in the window again. There is no such file on Windows: the system
encrypts secrets there (DPAPI).

## Internal files

The agent writes and reads these itself; there's no need to edit them.

| File | What's in it |
|---|---|
| `agents/<profile>.key` | The key the window and the commands on this machine use to talk to the agent (`600` permissions). If deleted, the agent creates a new one at startup |
| `agents/<profile>.json` | Where the running agent is: PID, port, start time, version. Appears at startup and is removed on exit |
| `agents/<profile>.lock` | A lock on Linux: a second agent for the same profile won't start |
| `agents/<profile>.after-update` | Whether the server was running before "Update on the server": a new agent version under systemd uses it to decide whether to start the server. Taken at startup |
| `agents/<profile>.remote.pfx` | The remote access certificate (see above) |
| `agents/<profile>.commands.json` | Server commands from its `/help` output, for hints in the window's console |
| `agents/<profile>.modupdates.json` | Which mod updates were already announced, so they aren't repeated |
| `ModConfigBackups/<profile>/` | Earlier versions of mod settings, saved before editing from the window |

`settings.json` and `notify-channels.json` in `data` are the eViSTool window's own settings. A server without a window
doesn't have them.
