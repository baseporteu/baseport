---
title: Command line
description: "The baseport wrapper, account and provider commands, and service control"
---

# Command line

The installer adds a `baseport` wrapper to the PATH. It runs the binary from the install directory regardless of the working directory, so the database, logs and uploads stay in one place. Commands that need root invoke sudo themselves.

## Commands

| Command | Action |
| --- | --- |
| `baseport help` | List the commands |
| `baseport version` | Print the version |
| `baseport status` | Whether Baseport is running, and where |
| `baseport doctor` | Check the install; each `warn` and `FAIL` line names its fix |
| `baseport logs [lines]` | Follow the log files, default 200 lines back, with the first-start login printed above the tail |
| `baseport update` | Stop, replace the binary with the latest release, restart the service |
| `baseport service [--urls URL]` | Install the systemd service (Linux, root) |
| `baseport start`, `stop`, `restart` | Control the service (Linux, root) |
| `baseport stop --force` | Stop the service and any foreground instance; succeeds when nothing runs |
| `baseport uninstall [--purge]` | Remove the service, program files and wrapper. `--purge` also deletes the data, after a confirmation |
| `baseport accounts ...` | Account operations, below |
| `baseport providers ...` | Wire listener control, below |

`logs`, `update`, `service`, `start`, `stop`, `restart`, `status`, `doctor` and `uninstall` belong to the wrapper script; the binary answers them with a pointer to the wrapper. Under systemd, `journalctl -u baseport` carries the same log output.

`doctor` checks: version and install path, whether the wrapper on the PATH belongs to this install, whether the database exists, the service state, whether the unit runs from the updated directory, and whether the service address answers.

`update` stops the running instance first: a running process keeps serving the replaced file, and on Windows the copy fails. Data is not changed.

`uninstall` keeps `baseport.db`, `baseport.key`, `appsettings.json`, `uploads/`, `backups/` and `log/`.

## Accounts

Operations the console refuses (see [Authentication](/docs/authentication#cli-only-operations)). They edit the database directly and work against a running instance.

| Command | Action |
| --- | --- |
| `accounts list` | Username, role, state and sign-in methods |
| `accounts promote <account>` | Make an admin |
| `accounts demote <account>` | Make a consumer; the last enabled admin is refused |
| `accounts password <account> <pw>` | Set a single-use password and revoke sessions |
| `accounts rename <account> <new>` | Change the username |
| `accounts link <account> <key> <subject>` | Attach a single sign-on identity and revoke sessions |
| `accounts unlink <account>` | Remove it; refused when the account has no password |
| `accounts totp-reset <account>` | Remove two-factor sign-in and revoke sessions |

`<account>` is a username or a unique e-mail address.

## Providers

| Command | Action |
| --- | --- |
| `providers status` | State and address of both listeners |
| `providers postgres enable [--port N] [--bind ADDR]` | Enable the Postgres listener |
| `providers postgres disable` | Disable it |
| `providers tds enable [--port N] [--bind ADDR]` | Enable the TDS listener |
| `providers tds disable` | Disable it |

A non-loopback `--bind` requires `Baseport:WireRemoteAccess`. See [Postgres and TDS clients](/docs/postgres-and-tds).

## Install location

| Installed as | Program and data | Wrapper |
| --- | --- | --- |
| Regular user | `~/.baseport` | `~/.local/bin` |
| root | `/opt/baseport` | `/usr/local/bin` |

Both paths are printed before the download. `BASEPORT_DIR` and `BASEPORT_BIN` override them:

```bash
BASEPORT_DIR=/srv/baseport BASEPORT_BIN=/usr/local/bin \
  curl -sSL https://raw.githubusercontent.com/baseporteu/baseport/main/Scripts/install.sh | sudo bash
```

The wrapper stores the chosen directory; `baseport update` uses it.

## Docker

Docker installs have no wrapper. A shell function in `~/.bashrc` or `~/.zshrc` forwards the same commands into the container:

```bash
baseport() {
  local compose="docker compose -f $HOME/baseport/docker-compose.yml"
  if [ "$1" = "update" ]; then
    $compose pull && $compose up -d
  else
    $compose exec baseport /app/Baseport "$@"
  fi
}
```
