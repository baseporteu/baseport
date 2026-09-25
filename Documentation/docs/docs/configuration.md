---
title: Configuration reference
description: "Every Baseport setting in appsettings.json and the environment"
---

# Configuration reference

Process settings are read from `appsettings.json` beside the binary, then from `appsettings.json` in the working directory, then from `appsettings.{Environment}.json` and environment variables. An environment variable replaces `:` with `__`: `Baseport:AdminAddress` becomes `Baseport__AdminAddress`.

Runtime settings (currency, time zone, authentication, jobs, sites, uploads, listeners) are managed under **Settings** in the console and stored in the database.

## Baseport section

| Key | Default | Effect |
| --- | --- | --- |
| `ConnectionString` | `Data Source=baseport.db` | SQLite database file. `uploads/` and `keys/` are created next to it |
| `TrustForwardedHeaders` | `false` | Read client address and scheme from `X-Forwarded-For` and `X-Forwarded-Proto`. Required behind a reverse proxy |
| `AdminAddress` | unset | Second listener for the console. Console and console-auth routes return `404` on the public port |
| `AllowInsecureSignIn` | `false` | Allow sign-in over plain HTTP off localhost; session cookies lose `Secure`. Logs a warning on start and fails `baseport doctor` |
| `WireRemoteAccess` | `false` | Allow the Postgres and TDS listeners to bind a non-loopback address |
| `PreviewSecret` | generated | Signing key for form preview links. Overrides the per-instance value stored in the database |

`TrustForwardedHeaders` accepts forwarded headers from a loopback proxy only (the ASP.NET Core default). A proxy on another host is not trusted; its clients share one rate-limit budget and sign-in sees plain HTTP. There is no setting for other proxy addresses yet.

## Host options

| Option | Example | Effect |
| --- | --- | --- |
| `--urls` | `http://0.0.0.0:5000` | Listening addresses, `;`-separated. Also `ASPNETCORE_URLS` |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Selects `appsettings.{Environment}.json` |

## Docker aliases

`docker-compose.yml` reads a `.env` file beside it:

| Variable | Setting | Default |
| --- | --- | --- |
| `BASEPORT_TAG` | Image tag | `latest` |
| `BASEPORT_PORT` | Published host port | `5000` |
| `BASEPORT_CONNECTION_STRING` | `Baseport:ConnectionString` | `Data Source=/data/baseport.db` |
| `BASEPORT_TRUST_FORWARDED_HEADERS` | `Baseport:TrustForwardedHeaders` | `false` |
| `BASEPORT_ADMIN_ADDRESS` | `Baseport:AdminAddress` | unset |

Other settings are passed as `Baseport__<Key>` entries under `environment:`.

## Logging

The `Serilog` section of `appsettings.json` configures logging: console output plus rolling files under `log/`, 14 files of up to 50 MB. Request outcomes log at Debug (success), Warning (4xx) and Error (5xx or exception).
