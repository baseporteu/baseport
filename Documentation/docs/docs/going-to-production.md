---
title: Going to production
description: "Configuration, the reverse proxy, backups and the switches that are off for a reason"
---

# Going to production

:::warning
Baseport is pre-alpha. The database format and the API surface still move between commits. Treat this page as what deployment will look like rather than a promise that today's file opens tomorrow.
:::

## Configuration

Settings live in `appsettings.json` beside the binary. Any `Baseport:*` setting is also an environment variable: replace the colon with a double underscore.

```ini
Baseport__ConnectionString=Data Source=/data/baseport.db
Baseport__TrustForwardedHeaders=true
Baseport__AdminAddress=0.0.0.0:5264
```

Everything else you would change while running lives in the console under **Settings** and is stored in the database.

## Listening address

`--urls` decides which interfaces Kestrel binds:

| Value | Reachable from |
| --- | --- |
| `http://localhost:5263` | The machine itself only |
| `http://0.0.0.0:5263` | Any interface, so anything that can route to the host |

`localhost` is the safe default and the one to keep if a reverse proxy on the same host is the only thing talking to Baseport. Use `0.0.0.0` when something on another machine connects directly.

Baseport serves plain HTTP and does not terminate TLS itself, so binding `0.0.0.0` on anything public means unencrypted traffic. Put a proxy in front of it.

## Behind a reverse proxy

Set `Baseport__TrustForwardedHeaders` to `true`. Rate limiting works off the client address, so without this every request looks like it came from the proxy and they all share one budget.

If you want the console off the public port altogether, give it its own address with `Baseport__AdminAddress` and only expose that port on loopback.

## Running it as a service

Install to `/opt/baseport`, then let `-i` write the service:

```bash
BASEPORT_DIR=/opt/baseport BASEPORT_BIN=/usr/local/bin \
  curl -sSL https://raw.githubusercontent.com/baseporteu/baseport/main/Scripts/install.sh | bash

sudo /usr/local/bin/baseport -i
```

Parameter `-i` requires root permissions as it creates a `baseport` system user and configures the services:

```bash
sudo /usr/local/bin/baseport -i --urls http://0.0.0.0:5263
```

Use the full path with `sudo`. Its `secure_path` does not include `~/.local/bin`, so a wrapper installed there will not resolve.

`-i` refuses rather than producing a unit that cannot start. It stops if a `baseport.service` already exists, if there is no systemd, or if the service account cannot read the install directory. That last one is why `/opt` and not `~/.baseport`: `/root` is mode 0700, so a `User=baseport` service cannot read anything inside it.

`/opt/baseport` holds the binary and the data together, because Baseport writes `baseport.db`, `log/` and `uploads/` relative to `WorkingDirectory`. Splitting the binary into `/opt` and data into `/var/lib` would need a second path the application does not have a concept of.

After `baseport update`, run `sudo baseport -d` to restart the service on the new binary.

:::warning
`baseport update` only updates the directory the wrapper was installed with. If you installed as root to `/root/.baseport` but your service runs from `/opt/baseport`, you will update a copy nothing is running. The installer warns when it detects this, and you can point it at the right place with `BASEPORT_DIR`.
:::

## Backups

Back up the directory Baseport runs in. What is in it:

| File | Why it matters |
| --- | --- |
| `baseport.db` | Everything: schema, records, accounts, settings |
| `baseport.key` | The ES256 key used to sign auth tokens. Lose it and every token you have issued stops working. |
| `uploads/` | Uploaded files, which are not stored in the database |
| `appsettings.json` | Your own configuration |

The `backup` job copies the SQLite file into `backups/` at 03:00 by default and keeps the five most recent. That is a local copy, not an offsite backup, so copy the directory somewhere else as well.

## Jobs

Maintenance jobs run on a scheduler you can see and edit under **Settings**. Backups, log cleanup, session cleanup, `PRAGMA optimize`, search index maintenance and anonymous account cleanup are on by default. File deletions is off, because an upload you have not attached to a record yet looks the same as an abandoned one.

You can also give a saved SQL query a cron expression and it will run on the same schedule. Give it a URL and each run posts the results there:

```json
{"query":"Daily revenue","ranAt":"2026-08-20T07:00:00Z","columns":["day","total"],"rows":[["2026-08-19","4210.50"]]}
```

Leave the URL empty and the row count is recorded against the query instead, so you can read it in the console. **Run now** lets you check the URL works without waiting for the schedule. The cron expression and the URL are both validated when you save, and the URL is checked again at run time, since a hostname that pointed at a public address when you saved it might not later.

## Things that are off by default

Each of these opens something up. Turn them on only when you need them.

| Switch | What it opens |
| --- | --- |
| Public authentication | `/auth` and `/api/auth/v1`, a second place accounts can sign in |
| Public registration | Lets visitors sign themselves up |
| Anonymous accounts | Lets an unauthenticated caller create an account |
| Postgres listener | The Postgres wire protocol, bound to `127.0.0.1:5432` by default |
| TDS listener | The SQL Server wire protocol, bound to `127.0.0.1:1433` by default |
| Proxy private targets | Lets outbound proxy requests reach your own network, including cloud metadata endpoints |

You can also control the two listeners from the shell:

```bash
baseport providers status
baseport providers postgres enable --port 5432 --bind 127.0.0.1
baseport providers tds disable
```

## Updating

`baseport update` replaces the binary and leaves `baseport.db`, `baseport.key`, `log/`, `uploads/`, `backups/` and your `appsettings.json` alone. It reinstalls into the same directory you first installed to.

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

If the wrapper is not on your PATH, running the installer again does the same thing.
