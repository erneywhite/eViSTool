# eViSTool on a Linux server

**English** · [Русский](linux.ru.md)

There is no eViSTool window on Linux yet: only the agent, `eViSTool.Agent`, runs there. It looks after the Vintage Story
server: starts it, brings it back after a crash, makes backups and scheduled restarts, and sends notifications. You
control it from the eViSTool window on Windows with a connection code, like any remote server (see
[Remote management](../README.md#remote-management) in the README). On the machine itself the agent is installed as a systemd
service and starts with the machine.

## What you need

- Ubuntu or Debian on x64 with systemd. Tested on Ubuntu Server 24.04.
- Root access (`sudo`).
- .NET 10 for the Vintage Story server itself. The agent doesn't need .NET: it ships with its own runtime.
- eViSTool on Windows to control the server from.

## The Vintage Story server

Install it the usual way, following the [official guide](https://wiki.vintagestory.at/Guide:Dedicated_Server) and the
`server.sh` script from the server archive. In short:

```sh
sudo apt install dotnet-runtime-10.0 screen procps
# download vs_server_linux-x64_<version>.tar.gz, unpack it and follow the guide:
sudo ./server.sh setup
```

`server.sh setup` creates the system user `vintagestory`, the game folder `/home/vintagestory/server` and the data
folder `/var/vintagestory/data` (world, config, mods, logs). The paths are set at the top of `server.sh`, and the agent
reads them from there.

If you started the server with `server.sh`, stop it before installing the service: from then on the agent starts it.
If you also run `server.sh start`, two servers end up on one world.

## Where to put eViSTool

In a separate folder next to the server, for example `/home/vintagestory/evistool`. Not in the game folder: the game is
updated with `rm -rf *` in its folder, and eViSTool would go away together with its settings. The agent keeps its
settings in the `data` folder next to it, just like on Windows.

## Installation

### 1. Download and unpack

The `eViSTool-<version>-linux-x64.tar.gz` archive is on the [ModDB page](https://mods.vintagestory.at/evistoollinux) and the
[Releases](https://github.com/erneywhite/eViSTool/releases) page. Inside is a single executable, `eViSTool.Agent`,
and the docs.

```sh
cd /tmp
wget https://github.com/erneywhite/eViSTool/releases/download/v0.10.0/eViSTool-0.10.0-linux-x64.tar.gz
sudo mkdir -p /home/vintagestory/evistool
sudo tar -xzf eViSTool-0.10.0-linux-x64.tar.gz -C /home/vintagestory/evistool
sudo chown -R vintagestory:vintagestory /home/vintagestory/evistool
cd /home/vintagestory/evistool
```

Don't skip the `chown`. The agent runs as `vintagestory`, the owner of the server data, and writes its settings, keys
and, when updating, new files into its folder.

### 2. Which user runs the commands

Run every command that changes the agent's settings as `vintagestory`: `sudo -u vintagestory ./eViSTool.Agent …`.
The agent encrypts secrets (notification tokens and the like) with a key belonging to the user it runs as. The key is
in `/home/vintagestory/.local/share/eViSTool/secret.key`, and the service uses the same key. A command run as root may
leave the `data` folder owned by root, and then the service can't write to it. `service install` notices this and
tells you which `chown` to run.

Only `service install` and `service uninstall` run as root: they write to `/etc/systemd/system`.

### 3. Find the server

```sh
sudo -u vintagestory ./eViSTool.Agent setup
```

The agent finds the server next to itself or through `server.sh`, remembers its folders (`data/agent.json`) and checks
permissions. If the server lives somewhere else, give the folders yourself: `--game /path/to/game --data /path/to/data`.

### 4. Remote access

```sh
sudo -u vintagestory ./eViSTool.Agent remote enable --host 203.0.113.5
sudo -u vintagestory ./eViSTool.Agent remote code
```

In `--host`, give the address your Windows computer will use to reach this machine: the local one on a home network
(like `192.168.1.20`), the public IP or a domain from the internet. The address goes into the connection code; you can
change it later with `remote host <address>`.

`remote code` prints the connection code. It holds the address, port, key and the server certificate's fingerprint, so
treat it like a password. If the code leaks, `remote new-key` changes the key and the old code stops working.

In the eViSTool window on Windows: "Settings" → "+ Server" → "Server on another computer?" → "Connect by code…",
paste the code, click "Check connection" and create the profile.

### 5. The remote access port

The agent picks the port at random (20000 to 60000) on the first `remote enable` and never changes it. To see it, run
`sudo -u vintagestory ./eViSTool.Agent remote status`. If the machine has a firewall, open the port:

```sh
sudo ufw allow 44731/tcp     # your port from remote status
```

The connection is encrypted (TLS), and without the key from the code the agent does nothing. Still, it's better not to
expose the port to the internet: a VPN (WireGuard, Tailscale) is safer, and then you connect to the machine's VPN
address. If you can't do without the internet, forward the port on your router.

The game port for players (`42420`, TCP and UDP) has nothing to do with this; open it as usual.

### 6. The systemd service

```sh
sudo ./eViSTool.Agent service install
```

The command checks that the service user can read the game and write to the server data and the eViSTool folder,
writes the unit `/etc/systemd/system/evistool.service`, enables it and starts it. The agent in the service runs as the
owner of the server data (usually `vintagestory`) and starts the server right away. At the end the command reports
what happened:

```
Service evistool is installed and running.
  agent: /home/vintagestory/evistool/eViSTool.Agent, user vintagestory, PID 93562
  server: /home/vintagestory/server, data: /var/vintagestory/data
  the server is starting together with the service (PID 93580)
After a reboot the service starts by itself.
Journal: sudo journalctl -u evistool -f
```

Options, if you need them:

- `--user <name>` runs the agent as another user. Without it, that's the owner of the server data. If everything
  belongs to root, the command asks you to name the user: it won't quietly run the server as root.
- `--name <name>` gives the service another name, for example for a second server on the same machine (with its own
  eViSTool folder).
- `--lang en` or `--lang ru` sets the language of the agent's messages that show up in the eViSTool window and of
  announcements to players. Without it the agent speaks the system language.
- `--game` and `--data`, if the server isn't where the agent finds it.

Running `service install` again is safe. With the same settings it changes nothing. With different ones it restarts the
service: the server stops with a world save and starts again.

## Day to day

You control the server from the eViSTool window. On the machine itself the main commands are there too:

```sh
sudo -u vintagestory ./eViSTool.Agent status            # what the server is doing
sudo -u vintagestory ./eViSTool.Agent stop              # stop the server, the world is saved
sudo -u vintagestory ./eViSTool.Agent start
sudo -u vintagestory ./eViSTool.Agent restart
sudo -u vintagestory ./eViSTool.Agent command "/time"   # a command to the server
```

They go through the running agent: `stop` stops only the server, the agent stays up and the window can still connect.
After a reboot the service starts the server again.

The whole service:

```sh
./eViSTool.Agent service status        # installed? enabled? running, and as whom?
systemctl status evistool
sudo systemctl stop evistool           # the agent stops the server with a world save, usually within seconds
sudo systemctl start evistool
sudo systemctl restart evistool
```

systemd allows five minutes for stopping: a big world takes a while to save. Whatever hasn't finished by then, systemd
ends by force.

### Logs

`sudo journalctl -u evistool -f` shows the agent's journal: where it found the server, which address it listens on,
why it didn't start. The server console is mirrored there too. The server's own log is in its data:
`/var/vintagestory/data/Logs/server-main.log`. The eViSTool window shows the console as well.

### If something crashed

If the server crashes, the agent brings it back, same as on Windows. If the agent itself crashes or gets killed
(`kill -9`), systemd ends the server right away, without a save, and five seconds later starts the agent again, which
starts the server. Whatever the server hadn't saved on its own is lost, so stop the service with `systemctl stop` or
the `stop` command.

If the agent keeps crashing (five starts in five minutes), systemd stops restarting it and marks the service as failed.
`sudo journalctl -u evistool` shows the reason. Once it's fixed, start the service again:

```sh
sudo systemctl reset-failed evistool
sudo systemctl start evistool
```

## Updating eViSTool

The easy way is from the eViSTool window on Windows: when your version is newer, the "Server" tab shows an "Update on
the server" button. The agent downloads the same version from GitHub, checks its checksum, stops the server with a
world save, replaces its own files and exits, and systemd starts the new version. If the server was running, it starts
again; if it was stopped, it stays stopped.

By hand: stop the service, unpack the new archive over the old one and start it. There is no `data` folder in the
archive, so the settings stay.

```sh
sudo systemctl stop evistool
sudo tar -xzf eViSTool-<version>-linux-x64.tar.gz -C /home/vintagestory/evistool
sudo chown -R vintagestory:vintagestory /home/vintagestory/evistool
sudo systemctl start evistool
```

## Updating the game

`server.sh` tells you to stop the server, clear the game folder (`rm -rf *`), unpack the new archive and put
`server.sh` back. With eViSTool it's the same, except you stop the service instead of the server:

```sh
sudo systemctl stop evistool
# update the game as server.sh says
sudo systemctl start evistool
```

eViSTool lives in its own folder, so the game update doesn't touch it.

## Uninstalling

```sh
sudo /home/vintagestory/evistool/eViSTool.Agent service uninstall
sudo rm -rf /home/vintagestory/evistool
sudo rm -rf /home/vintagestory/.local/share/eViSTool /home/vintagestory/.net/eViSTool.Agent
```

`service uninstall` stops the agent and the server (the world is saved) and removes the service. The second line
deletes eViSTool with its settings, the third the secrets key and the agent's unpacked libraries. The server and the
world stay where they are. If you opened the remote access port, close it: `sudo ufw delete allow 44731/tcp`.

## If it doesn't work

`service install` says what's in the way and what to do:

- If the service user lacks permissions, the command prints the `chown` that hands the folder over.
- If the agent for this profile is already running outside the service, stop it (Ctrl+C in its terminal or
  `kill <PID>`).
- If the server of this world is already running without the agent, stop it the way you started it: `server.sh stop`
  or `/stop` in its console.
- If the name `evistool` is taken by someone else's service, pick another one with `--name`.

If the Windows window can't connect, check `remote status` (is access on, which port), whether the port is open in
the firewall and on the router, and whether the address in the code fits where you connect from: a local address only
works inside the same network.

More about the agent's files and what's in them: [Configuration files](config.md).
