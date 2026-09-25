---
title: Going to production
description: "Configuration, reverse proxy, service install, backups, jobs and default-off switches"
---

# Going to production

:::warning
Baseport is pre-alpha. The database schema and API surface change between commits, without backward compatibility for existing data.
:::

## Configuration

Settings are read from `appsettings.json` beside the binary. Every `Baseport:*` setting is also an environment variable, with `__` replacing the colon.

```ini
Baseport__ConnectionString=Data Source=/data/baseport.db
Baseport__TrustForwardedHeaders=true
Baseport__AdminAddress=0.0.0.0:5264
```

`docker-compose.yml` forwards shorter aliases from a `.env` file to the same settings:

```ini
BASEPORT_CONNECTION_STRING=Data Source=/data/baseport.db
BASEPORT_TRUST_FORWARDED_HEADERS=true
BASEPORT_ADMIN_ADDRESS=0.0.0.0:5264
```

Runtime settings are managed under **Settings** in the console and stored in the database. Every key is listed in the [Configuration reference](/docs/configuration).

## Listening address

`--urls` sets the interfaces Kestrel binds:

| Value | Reachable from |
| --- | --- |
| `http://localhost:5000` | The host only |
| `http://0.0.0.0:5000` | Any interface |

`localhost` suits a reverse proxy on the same host. `0.0.0.0` is for direct connections from other machines. Baseport does not terminate TLS; a public `0.0.0.0` bind without a proxy carries unencrypted traffic.

## Reverse proxy

`Baseport__TrustForwardedHeaders=true` (`BASEPORT_TRUST_FORWARDED_HEADERS` in Docker) makes Baseport read the client address and scheme from the proxy's forwarded headers. Without it:

- rate limits key on the proxy address, so all clients share one budget;
- sign-in is refused with "Sign-in needs HTTPS on this address", because the request appears to be plain HTTP.

Session cookies are `Secure` unless the host is `localhost`, `127.0.0.1` or `[::1]`. Sign-in over plain HTTP on any other host is refused.

`Baseport__AllowInsecureSignIn=true` lifts both rules for a trusted network without TLS, such as a test server at `http://servername:5000`. Session cookies are then sent without `Secure` and can be taken over by anyone on the network path. The setting is config-only, logs a warning on every start, and fails `baseport doctor`. An SSH tunnel (`ssh -L 5000:localhost:5000 servername`, then `localhost:5000`) avoids it.

`Baseport__AdminAddress` (`BASEPORT_ADMIN_ADDRESS` in Docker) moves the console to a second listener; the console routes return `404` on the public port. Publish that port on loopback only; in `docker-compose.yml`, add an entry such as `"127.0.0.1:5264:5264"` under `ports:`.

## Service install

A root install uses `/opt/baseport` with the wrapper in `/usr/local/bin`. `service` writes the systemd unit:

```bash
curl -sSL https://raw.githubusercontent.com/baseporteu/baseport/main/Scripts/install.sh | sudo bash

sudo /usr/local/bin/baseport service
```

`service` requires root: it creates a `baseport` system user and writes the unit. Host options are passed through to `ExecStart`:

```bash
sudo /usr/local/bin/baseport service --urls http://0.0.0.0:5000
```

The full path is required with `sudo`, whose `secure_path` excludes `~/.local/bin`.

Rerunning `service` with different options rewrites the unit and restarts it. It refuses when systemd is missing or when the service account cannot read the install directory. A root install therefore defaults to `/opt`: `/root` is mode 0700 and unreadable to `User=baseport`.

Binary and data share `/opt/baseport`, because `baseport.db`, `log/` and `uploads/` are written relative to `WorkingDirectory`.

Service control, `update`, `doctor` and `uninstall`: see [Command line](/docs/command-line).

:::warning
`baseport update` targets the directory the wrapper was installed from. With a local install (`~/.baseport`) and a service in `/opt/baseport`, the update changes the inactive copy. The installer warns on this mismatch; `BASEPORT_DIR` selects the correct directory.
:::

## Backups

A backup covers the whole working directory:

| File | Contents |
| --- | --- |
| `baseport.db` | Schema, records, accounts, settings |
| `baseport.key` | ES256 token signing key. Without it every issued token is invalid |
| `uploads/` | Uploaded files, not stored in the database |
| `appsettings.json` | Local configuration |

The `backup` job copies the SQLite file into `backups/` daily at 03:00 and keeps the five most recent. `backups/` is on the same disk as the database; copy it off the host.

## Jobs

The scheduler runs maintenance jobs; schedules and switches are under **Settings**.

| Key | Default | Action |
| --- | --- | --- |
| `backup` | 03:00 daily | Copy the SQLite file into `backups/`, keep five |
| `heartbeat` | every 5 min | Record scheduler liveness |
| `logs-cleanup` | 04:00 daily | Prune audit rows past the log retention setting |
| `session-cleanup` | hourly | Remove expired sessions, sign-in codes and lockouts |
| `anonymous-cleanup` | 04:15 daily | Delete abandoned anonymous accounts, see [Authentication](/docs/authentication) |
| `query-optimizer` | 05:00 Sundays | `PRAGMA optimize` |
| `search-index` | 05:30 Sundays | Optimize the full text index, rebuild on drift |
| `file-deletions` | **off** | Delete uploads no record refers to, see [Files and uploads](/docs/files) |

`file-deletions` is off by default: an upload not yet attached to a record is indistinguishable from an abandoned one.

Saved SQL queries can run on the same scheduler; see [SQL and scheduled queries](/docs/sql-and-queries).

## Default-off switches

| Switch | Opens |
| --- | --- |
| Public authentication | `/auth` and `/api/auth/v1` |
| Public registration | Self sign-up |
| Anonymous accounts | Account creation by unauthenticated callers |
| Postgres listener | Postgres wire protocol, default `127.0.0.1:5432` |
| TDS listener | SQL Server wire protocol, default `127.0.0.1:1433` |
| Allow private targets | Outbound proxy, action and webhook requests to private networks, including cloud metadata endpoints |

## Wire listeners

Both wire protocols send the API token in cleartext. A listener binds only to a loopback address unless `Baseport:WireRemoteAccess` is `true` in `appsettings.json` (`Baseport__WireRemoteAccess=true` in Docker, where container loopback is unreachable from the host; the port also needs publishing). The console, the CLI and the listener at start all refuse a public bind address without it. A remote listener belongs behind a TLS tunnel.

| Limit | Value |
| --- | --- |
| Message size | 1 MB |
| Connections per listener | 32; the next is closed immediately |
| Handshake | 10 seconds |
| Statement | 10 seconds |

Listener setup and client connections: [Postgres and TDS clients](/docs/postgres-and-tds).

## AI agents

Record data and form submissions are untrusted input; an AI agent that reads them can be steered by visitor text. An agent account is a `consumer` with an API token limited to the required methods, an expiry date, and read rules restricted to its rows.

## Updating

`baseport update` replaces the binary in the original install directory. `baseport.db`, `baseport.key`, `log/`, `uploads/`, `backups/` and `appsettings.json` are not changed.

::: code-group
```sh [Linux]
baseport update
```
```powershell [Windows]
baseport update
```
```sh [Docker]
docker compose pull && docker compose up -d
```
:::

Without the wrapper on the PATH, rerunning the installer has the same effect.
