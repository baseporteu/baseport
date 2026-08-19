
<p align="center">
   <img src=".github/assets/baseport.webp" alt="Logo">
</p>

<p align="center">
  A single-executable backend for your data, with a type-safe REST API, realtime subscriptions, an admin console and embeddable forms, built on .NET 11 and SQLite.
<p>

# Baseport

<a href="LICENSE"><img src="https://img.shields.io/badge/license-EUPL%201.2-blue" alt="License - EUPL 1.2" /></a>
<img src="https://img.shields.io/badge/.NET-11-512BD4?logo=dotnet&logoColor=white" alt=".NET 11" />
<img src="https://img.shields.io/badge/database-SQLite-003B57?logo=sqlite&logoColor=white" alt="SQLite" />
<img src="https://img.shields.io/badge/status-pre--alpha-orange" alt="Status - pre-alpha" />

Meet Baseport: define your tables in the console and you get a typed REST API, live updates and an admin UI over them, without writing any of it. Point your mobile, web or desktop app at it and build the rest.

Built on the modern .NET stack, Baseport runs as one process over one SQLite file. There is no database server to run alongside it, allowing for blazing-fast product development. Copy the binary to a server, back up the file. That's all it takes for your deployment. Reads stay in single-digit milliseconds at a quarter of a million rows.

> **Pre-alpha.** Not yet v0.0.1. The database format and the API surface both still move between commits. Do not put production data in it.

## Installation

Pick whichever path fits your environment. Docker is the fastest way to get running; the published binary is the fastest way to deploy.

### Option A: Docker

```bash
docker compose up -d
docker compose logs baseport | grep "one-time admin password"
```

Browse to `http://localhost:5263/_/admin` and sign in using the username and (single-use) password from the startup logs. Rename the account with `baseport accounts rename <seeded> <yours>`. 

Set environment variables from a `.env` file if needed:

<details>

```ini
BASEPORT_TAG=latest
BASEPORT_PORT=5263
BASEPORT_TRUST_FORWARDED_HEADERS=false
```

Behind a reverse proxy set `BASEPORT_TRUST_FORWARDED_HEADERS` to `true`, or rate limiting puts every visitor in one bucket.

Anything else in `appsettings.json` is reachable the same way: any `Baseport:*` setting becomes an environment variable by replacing the colon with a double underscore. To keep the console off the public port entirely, add `Baseport__AdminAddress: "0.0.0.0:5264"` to the service's `environment` and publish that port to loopback only.

</details>

### Option B: Single executable

Every release ships one self-contained file per platform. `wwwroot`, `appsettings.json` and the .NET runtime all travel inside it, so there is nothing to install alongside it. Grab a tag from [Releases](https://github.com/hawkinslabdev/baseport/releases):

```bash
VERSION=v0.1.0
BASE=https://github.com/hawkinslabdev/baseport/releases/download/$VERSION

curl -LO $BASE/Baseport-$VERSION-linux-x64.tar.gz
curl -LO $BASE/Baseport-$VERSION-linux-x64.tar.gz.sha256
sha256sum -c Baseport-$VERSION-linux-x64.tar.gz.sha256

tar -xzf Baseport-$VERSION-linux-x64.tar.gz
./Baseport --urls http://localhost:5263
```

On Windows the asset is `Baseport-$VERSION-win-x64.zip`; unzip it and run `Baseport.exe` the same way.

Browse to `http://localhost:5263/_/admin` and sign in with the username and password the log printed on first start.

The binary writes `baseport.db`, `log/` and `uploads/` next to wherever you run it, so put it in its own directory. That directory is the backup.

### Option C: From source

Working on Baseport itself? Skip the publish step:

```bash
cd Source
dotnet run --project Baseport --urls http://localhost:5263
```

Tests and build:

```bash
dotnet test Baseport.slnx
dotnet build Baseport.slnx     # must be warning-free
node ../Scripts/test-frontend.js
```

To build the same single file the release workflow publishes:

```bash
dotnet publish Baseport/Baseport.csproj -c Release -r linux-x64 -o out
```

### Option D: Demo data

An empty console is hard to judge. Seed a workspace of products, customers, orders and order lines, with real references between them:

```bash
ADMIN_USER=<from the log> ADMIN_PASSWORD=<from the log> ./POPULATE.sh
```

That is 294,000 rows and takes about twenty seconds. In a hurry? Put `SCALE=0.05` in front for 15,000 rows in about a second.

## Configuration

#### Modelling your data

Create a table and add its fields. Fields are typed rather than plain text: number, currency, date, select, file, reference, plus `calculated` and `derived` values worked out on the server and never taken from the client.

Tick `Required`, `Unique` or `Identifier` and it is enforced on every write, whether the record arrives from a form, the REST API or the console.

Indexing is handled automatically.

#### Reading and writing it

Every table is private. You can opt in to expose one through the REST API. To consume it, issue a token to an account, which then gets full CRUD access:

```
GET    /api/v1/{apiName}/records          paged, searchable, sortable
POST   /api/v1/{apiName}/records
PATCH  /api/v1/{apiName}/records/{id}     merge
PUT    /api/v1/{apiName}/records/{id}     replace
DELETE /api/v1/{apiName}/records/{id}
GET    /api/v1/{apiName}/subscribe        Server-Sent Events
```

When you subscribe once, you'll receive updates for every write:

```bash
curl -N -H "Authorization: Bearer $TOKEN" \
  https://baseport.example.com/api/v1/sales-orders/subscribe

event: record
data: {"action":"create","id":"gAOPLyJDI5UU","record":{"OrderNo":"SO-100000",...}}
```

You choose the name a table publishes under, and it's separate use the name you work with in the console.

#### Authentication

Both sign-in screens take a password, or an OpenID Connect provider configured under **Settings → Authentication → Single sign-on**. Authelia, Authentik, Pocket ID, or anything publishing a discovery document works.

Add the provider, copy the redirect URL from the sheet, and register it at your provider:

```
https://baseport.example.com/api/auth/oidc/{key}/callback

```

Saving validates the discovery document immediately to catch wrong issuer URLs before saving. Authentication uses PKCE, verifying the `id_token` against the provider's JWKS.

Select where SSO appears: the console (`/_/auth`), your application users (`/auth`), or both. Accounts are matched on the provider's subject ID so directory renames do not break access. First sign-in automatically links existing accounts sharing the same username or verified email. Enable **Create accounts on first sign-in** to auto-provision new visitors as plain users.

Admin accounts are never linked automatically. Sign in once, grab the subject ID from the refusal log, and bind it manually:

```bash
baseport accounts link <username> <provider-key> <subject>
baseport accounts unlink <username>
```

#### Forms

A table can also be published as a form, so you get a public page without building a front end. Point a form at the table and the console hands you one line:

```html
<script src="https://baseport.example.com/embed.js?id=Kf3nQ8xR2vLm"></script>
```

The form will render exactly where you want it to. In order to customize (style) it you'll have to override the CSS variables on `.baserow-embed`.

You can determine how you want to expose your data. This can be a form or an overview. Decide which sites may embed them under **Settings → Sites**, one origin per line:

```
https://shop.example.com
https://portal.example.org
```

Leave it empty and any site may embed, which is what you want while developing. Though beware that if you configure this once, you'll have to make sure your whitelist is up to date.

## License

Licensed under the **EUPL 1.2** license. See [LICENSE](LICENSE) for details.
