---
title: Postgres and TDS clients
description: "Read published tables from Postgres and SQL Server clients over the wire protocols"
---

# Postgres and TDS clients

Two optional listeners speak the Postgres and SQL Server (TDS) wire protocols, so BI tools, drivers and command-line clients can query published tables. Both are read-only and off by default.

## Enabling

**Settings > Providers** in the console, or the shell:

```bash
baseport providers postgres enable --port 5432 --bind 127.0.0.1
baseport providers tds enable --port 1433 --bind 127.0.0.1
baseport providers status
```

| Listener | Default address |
| --- | --- |
| Postgres | `127.0.0.1:5432` |
| TDS | `127.0.0.1:1433` |

A non-loopback bind address requires `Baseport:WireRemoteAccess`; see [Going to production](/docs/going-to-production#wire-listeners).

## Connecting

The password is an account's API token. The username and database name are not checked.

| Client | Connection |
| --- | --- |
| psql | `psql "host=127.0.0.1 port=5432 user=baseport dbname=baseport sslmode=disable"`, token as password |
| Npgsql | `Host=127.0.0.1;Port=5432;Username=baseport;Password=<token>;SSL Mode=Disable` |
| SqlClient | `Server=127.0.0.1,1433;User Id=baseport;Password=<token>;Encrypt=False` |

Neither listener supports TLS: Postgres answers an SSL request with `N`, and TDS reports encryption as not supported. The token travels in cleartext; a remote listener belongs behind a TLS tunnel.

The token must be enabled and unexpired, as for the REST API. Its validity is re-checked during the session.

## Visible data

| Object | Contents |
| --- | --- |
| One view per table | Tables with **API enabled** and `GET` allowed on both the table and the token's methods |
| View name | The table name; tables whose name is not a plain identifier, starts with `_` or `sqlite_` are omitted |
| Columns | `id`, `created_at`, `updated_at`, then each field with a plain-identifier name. `password` and other secret fields are omitted |
| Rows | Filtered by the table's read rule for the calling account |
| Catalog | Postgres: `pg_catalog` and `information_schema`. TDS: `sys` and `INFORMATION_SCHEMA` |

The underlying `_records`, `_users` and other internal tables are not readable.

## Queries

Statements run on SQLite; the SQL dialect is SQLite's. Accepted statements start with `SELECT`, `WITH`, `VALUES` or `EXPLAIN`; one statement per query. Postgres `::type` casts are removed and `$1` parameters of the extended protocol are bound. Every `PRAGMA` is refused.

| Limit | Value |
| --- | --- |
| Rows per result | 200 |
| Statement time | 10 seconds |
| Message size | 1 MB |
| Connections per listener | 32 |
| Handshake | 10 seconds |
