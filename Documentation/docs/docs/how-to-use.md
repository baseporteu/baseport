---
title: How to use Baseport
description: "From an empty console to a working REST call in five steps"
---

# How to use Baseport

Five steps to get a table you can read and write over HTTP. Everything else in these pages builds on this.

## 1. Create a table

Go to **Tables**, add a table and add some fields. Every field has a type, so dates are stored as dates and currency amounts as currency amounts. See [Tables and fields](/docs/tables-and-fields).

## 2. Publish it

Tables are private by default. On the table's API panel, set an **API name** and turn **API enabled** on. The API name is what appears in the URL. It is separate from the name you see in the console, so you can rename a table without breaking anything already calling it.

## 3. Issue a token

Go to **Authentication**, open an account and generate an API token, choosing an expiry date. You see the token once. Only a hash of it is stored, so there is no way to look it up later. Copy it somewhere safe.

Use a `consumer` account for this. It can hold an API token but cannot sign in to the console.

## 4. Call it

```bash
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5263/api/v1/sales-orders/records?pageSize=5"
```

```json
{
  "rows": [
    {
      "id": "unique-id",
      "createdAt": "2026-08-20T09:14:00Z",
      "updatedAt": "2026-08-20T09:14:00Z",
      "data": {
        "OrderNo": "SO-100000",
        "Total": 421.5
      },
      "links": {
        "self": "/api/v1/sales-orders/records/unique-id",
        "collection": "/api/v1/sales-orders/records"
      }
    }
  ],
  "page": 1,
  "pageSize": 5,
  "total": 294000,
  "totalPages": 58800,
  "hasMore": true
}
```

Writes use the same path. All the routes are listed in the [Web APIs reference](/docs/api).

## 5. Watch it change

```bash
curl -N -H "Authorization: Bearer $TOKEN" \
  http://localhost:5263/api/v1/sales-orders/subscribe
```

```
event: record
data: {"action":"create","id":"unique-id","record":{"OrderNo":"SO-100000"}}
```

You get a Server-Sent Event for every write to that table. Add a record id to the end of the path to follow a single record instead.

## Both API switches have to be on

A request has to get past both of these before access rules are even considered:

- the account's own **API enabled** switch, which answers `401` when it is off
- the table's **API enabled** switch, which answers `403` when it is off

After that, [access rules](/docs/access-rules) decide which records the caller can see.

## Demo data

An empty console is hard to evaluate. From a source checkout, `./POPULATE.sh` creates products, customers, orders and order lines with references between them: roughly 294,000 rows in about twenty seconds. Prefix it with `SCALE=0.05` for 15,000 rows instead.
