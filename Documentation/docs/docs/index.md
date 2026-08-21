---
title: Introduction
description: "What Baseport is, and what you get once it is running"
---

# Introduction

Baseport is a single-executable backend powered by an embedded SQLite database. It eliminates the need for a separate database server. You define tables through the admin console, and every published table automatically receives a REST API, real-time updates via Server-Sent Events (SSE), and optional public forms.

:::warning
Baseport is pre-alpha. The database format and the API surface both still move between commits, so keep production data out of it for now.
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

The first start prints an admin username and a one-time password. You can use `baseport logs` to retrieve both the randomly generated username and password. Contineu to `http://localhost:5000/_/admin` and sign in. You'll be forced to set a new password.

`--urls http://localhost:5000` listens on loopback only, so nothing else on your network can reach it. Running on a server? Then you may want to reach from elsewhere, bind to every interface instead using `0.0.0.0` as your hostname.

Baseport speaks plain HTTP. For anything reachable beyond your own machine, put it behind a reverse proxy that terminates TLS. See [Going to production](/docs/going-to-production).

## The baseport command

The installer installs a lightweight `baseport` wrapper utility on your system PATH. The wrapper guarantees that application binaries execute within their designated root directory, ensuring databases, logs, and upload files remain consolidated regardless of where the command is issued.

```bash
baseport help             # Display available CLI subcommands
baseport accounts list    # List administrative accounts
baseport providers status # Check active authentication provider configurations
baseport logs             # View rolling log output
baseport update           # Upgrade binary to the latest release
sudo baseport service     # Configure or inspect the system daemon
sudo baseport restart     # Restart the background service
```

`baseport logs` follows the rolling log files in the install directory, 200 lines back by default. Pass a number for more or less: `baseport logs 50`. Under systemd, `journalctl -u baseport` shows the same output.

`baseport update` downloads the current release and replaces the binary, leaving your data alone.

The installer ignores the directory you run it from. Run as yourself it installs into `~/.baseport` with the wrapper in `~/.local/bin`; run as root it uses `/opt/baseport` and `/usr/local/bin`, which is what a service needs. It prints both before downloading anything. To choose somewhere else:

```bash
BASEPORT_DIR=/srv/baseport BASEPORT_BIN=/usr/local/bin \
  curl -sSL https://raw.githubusercontent.com/baseporteu/baseport/main/Scripts/install.sh | sudo bash
```

The wrapper remembers that directory, so `baseport update` returns to it rather than falling back to the default.

For Docker environments, define a shell function in your `~/.bashrc` or `~/.zshrc` to route commands directly to your container setup:

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

This mirror function allows commands like `baseport accounts list` and `baseport update` to operate identically to binary installations.

## Addresses

| What | Address | Who can use it |
| --- | --- | --- |
| Admin console | `/_/admin` | You, with a session cookie |
| REST API | `/api/v1/{apiName}/records` | Anything with a bearer token |
| End user sign-in | `/auth` and `/api/auth/v1` | Your application's users. Off by default |
| Forms | `/f/{formId}` and `/embed.js` | Anyone, per published form |
| OpenAPI document | `/api/openapi.json` | Anyone you give the URL to |

## File Layout & Data Management

Baseport writes operational files directly to its execution directory. Ensure your backup procedures capture this entire path:

* `baseport.db` — Main SQLite database storing schemas, user records, and application state.
* `baseport.key` — Private ES256 key used to sign JWT authentication tokens (restricted to owner-only permissions).
* `log/` — Rolling log file storage.
* `uploads/` — Binary assets uploaded via published forms or APIs.
* `backups/` — Snapshot backups of the SQLite database.

::: danger
If `baseport.key` is lost or deleted, all previously issued authentication tokens will immediately become invalid.
:::

## Next Steps

* Complete the onboarding guide in [How to use Baseport](https://www.google.com/search?q=/docs/how-to-use).
* Learn schema modeling in [Tables and fields](https://www.google.com/search?q=/docs/tables-and-fields).
