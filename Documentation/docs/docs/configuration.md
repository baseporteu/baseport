---
title: Configuration reference
description: "Every Baseport setting in appsettings.json and the environment"
---

# Configuration reference

Process settings are read in this order, later sources winning: `appsettings.json` beside the binary, `appsettings.json` in the working directory, `appsettings.{Environment}.json`, `.env` beside the binary, environment variables, command-line arguments. An environment variable replaces `:` with `__` (`Baseport__AdminAddress`) or uses its alias (`BASEPORT_ADMIN_ADDRESS`).

`baseport config` lists each setting with its source. `baseport config --check` and `baseport doctor` report `.env` lines that are invalid or override another source.

Runtime settings (currency, time zone, authentication, jobs, sites, uploads, listeners) are managed under **Settings** in the console and stored in the database.

## Baseport section

| Key | Default | Effect |
| --- | --- | --- |
| `ConnectionString` | `Data Source=baseport.db` | SQLite database file. `uploads/` and `keys/` are created next to it |
| `TrustForwardedHeaders` | `false` | Read client address and scheme from `X-Forwarded-For` and `X-Forwarded-Proto`. Required behind a reverse proxy |
| `AdminAddress` | unset | Second listener for the console. Console and console-auth routes return `404` on the public port |
| `AllowInsecureSignIn` | `false` | Allow sign-in over plain HTTP off localhost; session cookies lose `Secure`. Logs a warning on start and fails `baseport doctor` |
| `AllowRemoteProviders` | `false` | Allow the Postgres and TDS listeners to bind a non-loopback address |

`TrustForwardedHeaders` accepts forwarded headers from a loopback proxy only (the ASP.NET Core default). A proxy on another host is not trusted; its clients share one rate-limit budget and sign-in sees plain HTTP. There is no setting for other proxy addresses yet.

## Host options

| Option | Example | Effect |
| --- | --- | --- |
| `--urls` | `http://0.0.0.0:5000` | Listening addresses, `;`-separated. Also `ASPNETCORE_URLS` |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Selects `appsettings.{Environment}.json` |

## .env

The installer creates `.env` in the install directory with every setting commented out; updates keep it. `docker-compose.yml` passes the same file to the container. A line is `NAME=value`, with an alias or a `Baseport__<Key>` name.

| Alias | Setting |
| --- | --- |
| `BASEPORT_URLS` | `--urls` |
| `BASEPORT_CONNECTION_STRING` | `Baseport:ConnectionString` |
| `BASEPORT_TRUST_FORWARDED_HEADERS` | `Baseport:TrustForwardedHeaders` |
| `BASEPORT_ADMIN_ADDRESS` | `Baseport:AdminAddress` |
| `BASEPORT_ALLOW_INSECURE_SIGNIN` | `Baseport:AllowInsecureSignIn` |
| `BASEPORT_ALLOW_REMOTE_PROVIDERS` | `Baseport:AllowRemoteProviders` |
| `BASEPORT_TAG` | Image tag, Docker only |
| `BASEPORT_PORT` | Published host port, Docker only |

`BASEPORT_URLS` has no effect under `baseport service`, which passes `--urls`.

## Logging

The `Serilog` section of `appsettings.json` configures logging: console output plus rolling files under `log/`, 14 files of up to 50 MB. Request outcomes log at Debug (success), Warning (4xx) and Error (5xx or exception).
