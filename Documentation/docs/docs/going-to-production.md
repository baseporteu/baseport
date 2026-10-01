---
title: Going to production
description: "Configuration, reverse proxy, service install, backups, jobs and default-off switches"
---

# Going to production

:::warning
Baseport is pre-alpha. The database schema and API surface change between commits, without backward compatibility for existing data.
:::

## Configuration

Settings go in `.env` in the install directory. Docker reads the `.env` beside `docker-compose.yml`.

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

`BASEPORT_TRUST_FORWARDED_HEADERS=true` makes Baseport read the client address and scheme from the proxy's forwarded headers. Without it:

- rate limits key on the proxy address, so all clients share one budget;
- sign-in is refused with "Sign-in needs HTTPS on this address", because the request appears to be plain HTTP.

Session cookies are `Secure` unless the host is `localhost`, `127.0.0.1` or `[::1]`. Sign-in over plain HTTP on any other host is refused.

`BASEPORT_ALLOW_INSECURE_SIGNIN=true` lifts both rules for a trusted network without TLS, such as a test server at `http://servername:5000`. Session cookies are then sent without `Secure` and can be taken over by anyone on the network path. The setting is config-only, logs a warning on every start, and fails `baseport doctor`. An SSH tunnel (`ssh -L 5000:localhost:5000 servername`, then `localhost:5000`) avoids it.

`BASEPORT_ADMIN_ADDRESS` moves the console to a second listener; the console routes return `404` on the public port. Publish that port on loopback only; in `docker-compose.yml`, add an entry such as `"127.0.0.1:5264:5264"` under `ports:`.

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
| `keys/` | Encryption keys for secrets, proxy tokens and provider client secrets |
| `uploads/` | Uploaded files, not stored in the database |
| `appsettings.json` | Local configuration |

The `backup` job writes an archive to `backups/` daily at 03:00 and keeps the five most recent; **Settings > Backups** triggers one on demand and sets the retention. An archive is a zip holding `baseport.db`, `uploads/`, `keys/`, `baseport.key` and a `manifest.json` with the database checksum. `appsettings.json` and `.env` are not included.

Each snapshot passes `PRAGMA integrity_check` before it is archived. A failed check keeps the older archives and fails the job. Retention prunes only after a good archive exists, and one backup runs at a time.

`backups/` is on the same disk as the database; copy it off the host, or enable **Export to S3** (**Settings > Backups**: bucket, region, optional endpoint for S3-compatible storage, prefix, access key and secret). Keep the bucket private: an archive holds password hashes, encrypted proxy tokens and the `keys/` ring that decrypts them. The S3 copy leaves out `baseport.key`: whoever holds that key can sign tokens for any account. Restoring an S3 archive therefore ends every session.

Restore an archive into a scratch directory once a quarter, start Baseport on it and sign in. An archive that was never restored is not a verified backup.

### Recovery point

Daily archives can lose up to 24 hours of changes. For a shorter recovery point, run [Litestream](https://litestream.io) beside Baseport. It streams `baseport.db` to object storage or a file path within seconds of each write:

```yaml
# /etc/litestream.yml
dbs:
  - path: /opt/baseport/baseport.db
    replica:
      url: s3://my-bucket/baseport
```

```bash
litestream replicate                                   # runs beside the service
litestream restore -o /opt/baseport/baseport.db /opt/baseport/baseport.db
```

Litestream replicates the database only. Uploads, `keys/` and `baseport.key` still come from the archives: restore the newest archive first, then the Litestream copy of `baseport.db` over it, with Baseport stopped. Baseport issues no checkpoints of its own, which Litestream requires. On SELinux hosts, a containerised Litestream needs its volumes mounted with `:z`.

### Restore

```bash
baseport restore backups/baseport-20261001030000000-AbCd.zip
```

The wrapper stops the service, runs the restore and starts the service again. The binary refuses while a Baseport process holds the database, and refuses an archive that:

- fails its checksum or `PRAGMA integrity_check`;
- holds a file its manifest does not list, a path outside the data directory, or a file larger than listed;
- carries a migration this version does not know (update Baseport first).

The current `baseport.db`, `uploads/`, `keys/` and `baseport.key` move to `restore-previous-<time>/` beside them; nothing is deleted. `--yes` skips the confirmation. A `.db` snapshot from an older release restores the database only. Pending migrations run on the next start.

### Integrity

Every start runs `PRAGMA quick_check`. After a crash or power loss (a `baseport.db-wal` file left behind), the check runs before the server listens, and a damaged database stops startup with one line that names the file. After a clean shutdown the server listens at once and the check runs in the background; damage stops the server with the same line. The check reads the whole file: about 11 seconds for a 700 MB database on disk. `baseport check` runs it on demand and exits 1 on damage; `baseport doctor` reports it.

### One instance per database

A server holds an exclusive lock on `baseport.lock` beside the database for its lifetime. A second server on the same database exits with one line. The lock is advisory: network file systems such as NFS do not honour it, and `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` turns it off (`doctor` warns when set).

## Health and limits

| Route | Answer |
| --- | --- |
| `GET /api/healthz` | `200` while the process runs |
| `GET /api/readyz` | `200` when the database answers and no migration is pending, otherwise `503` |

Both are anonymous, answer on the public and the admin listener, and are limited to 120 requests per minute per client. The container image runs `/api/readyz` as its `HEALTHCHECK`; `baseport doctor` calls it too.

| Limit | Value |
| --- | --- |
| Request time | 30 s, then `504` |
| Uploads, record writes with files, imports, backups, proxy import | 10 min |
| Realtime streams (`/subscribe`) | none |
| Concurrent HTTP connections | 1000, `Baseport:MaxConnections` |
| Shutdown | 30 s: realtime streams close at once, a running job finishes within the window |

Statements on the SQL page, the wire listeners and scheduled queries keep their own 10 s limit.

## Metrics

Baseport publishes a `Baseport` meter. Read it on the host with [dotnet-counters](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-counters):

```bash
dotnet-counters monitor -n Baseport --counters Baseport,Microsoft.AspNetCore.Hosting
```

| Instrument | Unit | Meaning |
| --- | --- | --- |
| `baseport.sse.subscribers` | | Open realtime subscriptions |
| `baseport.backup.age` | s | Age of the newest archive |
| `baseport.record.write.duration` | ms | Time to save a change to records |
| `baseport.audit.queue` | | Audit entries waiting to be written |
| `baseport.job.failures` | | Failed job runs, tagged by `job` |

A histogram or counter appears after its first measurement.

## Jobs

The scheduler runs maintenance jobs; schedules and switches are under **Settings**.

| Key | Default | Action |
| --- | --- | --- |
| `backup` | 03:00 daily | Archive the database, uploads and keys into `backups/`, keep five |
| `heartbeat` | every 5 min | Record scheduler liveness |
| `logs-cleanup` | 04:00 daily | Prune audit rows past the log retention setting |
| `session-cleanup` | hourly | Remove expired sessions, sign-in codes and lockouts |
| `anonymous-cleanup` | 04:15 daily | Delete abandoned anonymous accounts, see [Authentication](/docs/authentication) |
| `query-optimizer` | 05:00 Sundays | `PRAGMA optimize` |

| `search-index` | 05:30 Sundays | Optimize the full text index, rebuild on drift |
| `file-deletions` | **off** | Delete uploads no record refers to, see [Files and uploads](/docs/files) |

`file-deletions` is off by default: an upload not yet attached to a record is indistinguishable from an abandoned one. When on, it skips uploads younger than one hour and removes unfinished uploads older than that.

Clones are listed above the jobs; see [Import](/docs/import#clones).

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

Both wire protocols send the API token in cleartext. A listener binds only to a loopback address unless `BASEPORT_ALLOW_REMOTE_PROVIDERS=true`. In Docker, container loopback is unreachable from the host, so the setting and a published port are both required. The console, the CLI and the listener at start all refuse a public bind address without it. A remote listener belongs behind a TLS tunnel.

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

## Verifying a release

Each release archive carries a build provenance attestation and an SPDX SBOM (`Baseport-<tag>-<rid>.spdx.json`) on the release page. The container image carries both as registry attestations.

```bash
gh attestation verify Baseport-v0.1.0-linux-x64.tar.gz --repo baseporteu/baseport
```
