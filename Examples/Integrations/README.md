# Wire protocol quick start

Baseport speaks the PostgreSQL and SQL Server wire protocols. Any driver for
those databases reads your tables as real tables with real columns.

1. Settings → Providers: turn the listener on. Both are off by default. Postgres
   `5432`, SQL Server `1433`, bound to `127.0.0.1`.
2. Authentication → your account → generate an API token. Shown once. That token
   is the password; your account name is the username.
3. Run one of these single-file .NET 11 apps:

```bash
dotnet run Postgres.cs <token>
dotnet run SqlServer.cs <token>
```

Both print ten rows from a table named `Customers`. Point them at one you have.
`BASEPORT_HOST`, `BASEPORT_USER`, `BASEPORT_PG_PORT`, `BASEPORT_TDS_PORT`
override the defaults.

## Connection strings

```
Host=127.0.0.1;Port=5432;Username=admin;Password=<token>;Database=baseport;SSL Mode=Disable
Server=127.0.0.1,1433;User ID=admin;Password=<token>;Database=baseport;Encrypt=Optional
```

Npgsql also needs type loading off (`ConfigureTypeLoading(o => o.EnableTypeLoading(false))`).
There is no TLS: keep the listeners on loopback.

## Limits

Read-only, one statement, 200 rows max. `SELECT`, `WITH`, `VALUES`, `EXPLAIN`,
`PRAGMA`. Writes go through the REST API or the console.

The engine underneath is SQLite, `LIMIT` works everywhere and `SELECT TOP n`
is rewritten for you. Parameters work on the PostgreSQL side; on SQL Server they
become RPC calls, which the listener does not implement yet.

DBeaver connects to both. SSMS and the VS Code MSSQL extension do not: their
object explorers need RPC.
