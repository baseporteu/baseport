---
title: Introduction
description: "What Baseport is, and what you get once it is running"
---

# Introduction

Baseport is one executable with SQLite inside it, so there is no database server to run alongside it. You define tables in the admin console. Publish one and you get a REST API for it, live updates over Server-Sent Events, and public forms if you want them.

:::warning
Baseport is pre-alpha. The database format and the API surface both still move between commits, keep production data out of it for now.
:::

## Install and start it

::: code-group
```sh [Linux]
curl -sSL https://raw.githubusercontent.com/baseporteu/baseport/main/Scripts/install.sh | bash
```
```powershell [Windows]
iwr https://raw.githubusercontent.com/baseporteu/baseport/main/Scripts/install.ps1 | iex
```
```yaml [Docker]
services:
  baseport:
    image: ghcr.io/baseporteu/baseport:latest
    container_name: baseport
    restart: unless-stopped
    ports:
      - "5000:5000"
    volumes:
      - baseport-data:/data

volumes:
  baseport-data:
```
:::

Releases ship `linux-x64` and `win-x64` builds.

The first start prints an admin username and a one-time password. `baseport logs` shows both again if you missed them. Go to `http://localhost:5000/_/admin`, sign in, and you will be asked to set a new password straight away.

`--urls http://localhost:5000` listens on loopback only, so nothing else on your network can reach it. On a server you probably want to reach it from elsewhere, so bind every interface with `0.0.0.0` instead.

Baseport speaks plain HTTP. For anything reachable beyond your own machine, put it behind a reverse proxy that terminates TLS. See [Going to production](/docs/going-to-production).

## The baseport command

The installer puts a small `baseport` wrapper on your PATH. It runs the binary from the install directory no matter where you are when you type it, so the database, logs and uploads always end up in the same place.

```bash
baseport help             # List the subcommands
baseport accounts list    # List the accounts
baseport providers status # Show which sign-on providers are on
baseport status           # Say whether Baseport is running, and where
baseport doctor           # Check the install and name what is wrong
baseport logs             # View rolling log output
baseport update           # Upgrade binary to the latest release
sudo baseport service     # Install the systemd service
sudo baseport start       # Start that service
sudo baseport stop        # Stop it
sudo baseport restart     # Restart it
baseport uninstall        # Remove Baseport, keeping your data
```

Anything that needs root asks sudo for you instead of telling you to retype the command.

`baseport logs` follows the rolling log files in the install directory, 200 lines back by default. Pass a number for more or less: `baseport logs 50`. Under systemd, `journalctl -u baseport` shows the same output.

`baseport update` downloads the current release and replaces the binary, leaving your data alone.

`baseport doctor` is the first thing to run when something is off. It prints one line per check: the version and where it lives, whether the wrapper on your PATH is this one, whether the database is there, what the service is doing, and whether anything answers on the address the service listens on. Every `warn` and `FAIL` line names the command that fixes it.

`baseport uninstall` removes the service, the program files and the `baseport` command, and leaves `baseport.db`, `baseport.key`, `appsettings.json`, `uploads/`, `backups/` and `log/` where they are. `baseport uninstall --purge` deletes those too, and asks first.

The installer ignores the directory you run it from. Run as yourself it installs into `~/.baseport` with the wrapper in `~/.local/bin`; run as root it uses `/opt/baseport` and `/usr/local/bin`, which is what a service needs. It prints both before downloading anything. To choose somewhere else:

```bash
BASEPORT_DIR=/srv/baseport BASEPORT_BIN=/usr/local/bin \
  curl -sSL https://raw.githubusercontent.com/baseporteu/baseport/main/Scripts/install.sh | sudo bash
```

The wrapper remembers that directory, `baseport update` returns to it instead of falling back to the default.

On Docker there is no wrapper, so put a shell function in `~/.bashrc` or `~/.zshrc` that sends the same commands into the container:

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

`baseport accounts list` and `baseport update` then work the way they do on a normal install.

## Addresses

| What | Address | Who can use it |
| --- | --- | --- |
| Admin console | `/_/admin` | You, with a session cookie |
| REST API | `/api/v1/{apiName}/records` | Anything with a bearer token |
| End user sign-in | `/auth` and `/api/auth/v1` | Your application's users. Off by default |
| Forms | `/f/{formId}` and `/embed.js` | Anyone, per published form |
| OpenAPI document | `/api/openapi.json` | Anyone you give the URL to |

## What is on disk

Everything Baseport writes goes in the directory it runs from. Back up the whole directory, not just the database:

| File | What it is |
| --- | --- |
| `baseport.db` | The database: your schema, your records, accounts and settings |
| `baseport.key` | The ES256 key that signs auth tokens, readable only by the owner |
| `log/` | Rolling log files |
| `uploads/` | Files uploaded through forms or the API |
| `backups/` | Database snapshots |

::: danger
Lose `baseport.key` and every token already issued stops working. Everyone signs in again.
:::

## Next

- [How to use Baseport](/docs/how-to-use) walks from an empty console to a working REST call.
- [Tables and fields](/docs/tables-and-fields) covers the field types and what each one enforces.
