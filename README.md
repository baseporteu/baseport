
<p align="center">
   <img src=".github/assets/baseport.webp" alt="Logo">
</p>

<p align="center">
  A single-executable backend for your data, with a type-safe REST API, realtime subscriptions, an admin console and embeddable forms, built on .NET 11 and SQLite.
<p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-EUPL%201.2-blue" alt="License - EUPL 1.2" /></a>
  <img src="https://img.shields.io/badge/.NET-11-512BD4?logo=dotnet&logoColor=white" alt=".NET 11" />
  <img src="https://img.shields.io/badge/database-SQLite-003B57?logo=sqlite&logoColor=white" alt="SQLite" />
  <img src="https://img.shields.io/badge/status-pre--alpha-orange" alt="Status - pre-alpha" />
</p>

# Baseport

Meet Baseport: define your tables in the console and you get a typed REST API, live updates and an admin UI over them, without writing any of it. Point your mobile, web or desktop app at it and build the rest.

Built on the modern .NET stack, Baseport runs as one process over one SQLite file. There is no database server to run alongside it, no cache to keep warm and no broker in the middle. Copy the binary to a server, back up the file, and that is the deployment. Reads stay in single-digit milliseconds at a quarter of a million rows, so a cache would have nothing to add anyway.

> **Pre-alpha.** Not yet v0.0.1. The database format and the API surface both still move between commits. Do not put production data in it.

## Installation

Pick whichever path fits your environment. The published binary is the fastest way to get running.

### Option A: Single executable

Publishing produces one self-contained file. `wwwroot`, `appsettings.json` and the .NET runtime all travel inside it.

```bash
cd Source
dotnet publish Baseport/Baseport.csproj -c Release -o out
```

Copy `out/Baseport` anywhere and run it:

```bash
./Baseport --urls http://localhost:5263
```

Browse to `http://localhost:5263/_/admin` and sign-in with the credentials shown in the log for the `admin` account.

### Option B: From source

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

### Option C: Demo data

An empty console is hard to judge. Seed a workspace of products, customers, orders and order lines, with real references between them:

```bash
ADMIN_PASSWORD=<from the log> ./POPULATE.sh
```

That is 294,000 rows and takes about twenty seconds. In a hurry? Put `SCALE=0.05` in front for 15,000 rows in about a second.

## Configuration

#### Modelling your data

Create a table and add its fields. Fields are typed rather than plain text: number, currency, date, select, file, reference, plus `calculated` and `derived` values worked out on the server and never taken from the client.

Tick `Required`, `Unique` or `Identifier` and it is enforced on every write, whether the record arrives from a form, the REST API or the console.

You won't have to create database index manually. Though if you want, you can flag a field and Baseport builds the column and the index behind it as it'll handle this for you.

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

When you subscribe once, you'll receive updates for every write, even from form submissions:

```bash
curl -N -H "Authorization: Bearer $TOKEN" \
  https://baseport.example.com/api/v1/sales-orders/subscribe

event: record
data: {"action":"create","id":"gAOPLyJDI5UU","record":{"OrderNo":"SO-100000",...}}
```

You choose the name a table publishes under, and it's separate use the name you work with in the console. So you can rename a table whenever you want without breaking anything you already shipped. You'll find the browsable reference at `/docs` and the OpenAPI 3.2 document at `/api/openapi.json`.

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
