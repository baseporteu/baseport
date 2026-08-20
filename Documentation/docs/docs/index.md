---
title: Introduction
description: "What Baseport is, and what you get once it is running"
---

# Introduction

Baseport is a backend that runs as one executable against one SQLite file. There is no separate database server to install. You define your tables in the admin console, and any table you publish gets a REST API, live updates over Server-Sent Events, and optionally a public form.

:::warning
Baseport is pre-alpha. The database format and the API surface both still move between commits, so keep production data out of it for now.
:::

## Install and start it

::: code-group
```sh [Linux]
curl -sSL https://raw.githubusercontent.com/baseporteu/baseport/main/Scripts/install.sh | bash
baseport --urls http://localhost:5263
```
```powershell [Windows]
iwr https://raw.githubusercontent.com/baseporteu/baseport/main/Scripts/install.ps1 | iex
baseport --urls http://localhost:5263
```
```yaml [Docker]
services:
  baseport:
    image: ghcr.io/baseporteu/baseport:latest
    container_name: baseport
    restart: unless-stopped
    ports:
      - "5263:5263"
    volumes:
      - baseport-data:/data

volumes:
  baseport-data:
```
:::

Releases ship `linux-x64` and `win-x64` builds.

The first start prints an admin username and a one-time password. On Docker, read it with `docker compose logs baseport`. The username is `admin-` followed by eight random characters, not plain `admin`, so it cannot be guessed.

Open `http://localhost:5263/_/admin` and sign in.

`--urls http://localhost:5263` listens on loopback only, so nothing else on your network can reach it. Running on a server? Then you may want to reach from elsewhere, bind to every interface instead using `0.0.0.0` as your hostname.

Baseport speaks plain HTTP. For anything reachable beyond your own machine, put it behind a reverse proxy that terminates TLS. See [Going to production](/docs/going-to-production).

## The baseport command

The installer puts a small `baseport` wrapper on your PATH. It always runs the binary from its own directory, so the database, logs and uploads stay in one place no matter where you call it from:

```bash
baseport help
baseport accounts list
baseport providers status
baseport logs
baseport update
sudo baseport -d
```

`baseport logs` follows the rolling log files in the install directory, 200 lines back by default. Pass a number for more or less: `baseport logs 50`. Under systemd, `journalctl -u baseport` shows the same output.

`baseport update` downloads the current release and replaces the binary, leaving your data alone.

The installer ignores the directory you run it from. It installs into `~/.baseport` and puts the wrapper in `~/.local/bin`, and it prints both before downloading anything. To choose somewhere else, and you should on a server:

```bash
BASEPORT_DIR=/opt/baseport BASEPORT_BIN=/usr/local/bin \
  curl -sSL https://raw.githubusercontent.com/baseporteu/baseport/main/Scripts/install.sh | bash
```

The wrapper remembers that directory, so `baseport update` returns to it rather than falling back to the default.

On Docker there is no wrapper to install, so define the same command as a shell function. Point it at wherever you keep the compose file:

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

Put that in your `~/.bashrc` or `~/.zshrc` and `baseport accounts list` and `baseport update` work the same as they do on a binary install.

## Addresses

| What | Address | Who can use it |
| --- | --- | --- |
| Admin console | `/_/admin` | You, with a session cookie |
| REST API | `/api/v1/{apiName}/records` | Anything with a bearer token |
| End user sign-in | `/auth` and `/api/auth/v1` | Your application's users. Off by default |
| Forms | `/f/{formId}` and `/embed.js` | Anyone, per published form |
| OpenAPI document | `/api/openapi.json` | Anyone you give the URL to |

## Files on disk

Baseport writes `baseport.db`, `baseport.key`, `log/`, `uploads/` and `backups/` into whatever directory you run it from. Give it a directory of its own, and back up that directory.

`baseport.key` holds the ES256 key used to sign auth tokens. It is created readable only by the owner. If you lose it, every token you have already issued stops working.

## Next

Read [How to use Baseport](/docs/how-to-use) to get something working, then [Tables and fields](/docs/tables-and-fields).
