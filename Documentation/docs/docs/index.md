---
title: Introduction
description: "What Baseport is and what a running instance provides"
---

# Introduction

Baseport is a single executable with an embedded SQLite database; no separate database server is required. Tables are defined in the admin console. A published table gets a REST API, live updates over Server-Sent Events, and optional public forms.

:::warning
Baseport is pre-alpha. The database format and the API surface change between commits. Do not store production data in it yet.
:::

## Install and start

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

The first start writes an admin username and a one-time password to the log; `baseport logs` prints both again. Sign in at `http://localhost:5000/_/admin`. The one-time password must be replaced before the console loads.

`--urls http://localhost:5000` binds loopback only. `0.0.0.0` binds every interface.

Baseport serves plain HTTP. An instance reachable from other machines belongs behind a TLS-terminating reverse proxy; see [Going to production](/docs/going-to-production).

## The baseport command

The installer adds a `baseport` wrapper to the PATH for status, logs, updates and service control. See [Command line](/docs/command-line).

## Addresses

| Surface | Address | Access |
| --- | --- | --- |
| Admin console | `/_/admin` | Operators, session cookie |
| REST API | `/api/v1/{apiName}/records` | Anything with a bearer token |
| End user sign-in | `/auth` and `/api/auth/v1` | Application users. Off by default |
| Forms | `/f/{formId}` and `/embed.js` | Anyone, per published form |
| OpenAPI document | `/api/openapi.json` | Anonymous |

## Files on disk

Baseport writes everything to its working directory. A backup covers the whole directory, not only the database:

| File | Contents |
| --- | --- |
| `baseport.db` | Schema, records, accounts and settings |
| `baseport.key` | The ES256 key that signs auth tokens, readable only by the owner |
| `log/` | Rolling log files |
| `uploads/` | Files uploaded through forms or the API |
| `backups/` | Database snapshots |

::: danger
Losing `baseport.key` invalidates every issued token. All users must sign in again.
:::

## Next

- [How to use Baseport](/docs/how-to-use): from an empty console to a working REST call.
- [Tables and fields](/docs/tables-and-fields): field types and their validation.
- [Going to production](/docs/going-to-production): proxy, service, backups.
