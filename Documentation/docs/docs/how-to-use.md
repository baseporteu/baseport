---
title: How to use Baseport
description: "From an empty console to a working REST call in five steps"
---

# How to use Baseport

Five steps from an empty console to a table that is readable and writable over HTTP.

## 1. Create a table

**Tables**: add a table and its fields. Each field has a type that determines storage and validation, for example dates as dates and amounts as currency. See [Tables and fields](/docs/tables-and-fields).

## 2. Publish it

Tables are private by default. On the table's API panel, set an **API name** and turn on **API enabled**. The API name forms the URL and is independent of the console name, so renaming a table does not change its routes.

## 3. Issue a token

**Authentication**: open an account and generate an API token with an expiry date. The token is displayed once; only its hash is stored.

Use a `consumer` account: it holds an API token and has no console access.

## 4. Call it

```bash
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5000/api/v1/sales-orders/records?pageSize=5"
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

Writes use the same path. The [Web APIs reference](/docs/api) lists every route.

## 5. Watch it change

```bash
curl -N -H "Authorization: Bearer $TOKEN" \
  http://localhost:5000/api/v1/sales-orders/subscribe
```

```
event: record
data: {"action":"create","id":"unique-id","record":{"OrderNo":"SO-100000"}}
```

Every write to the table produces one Server-Sent Event. Appending a record id to the path limits the stream to that record.

## API switches

Two switches are checked before access rules:

- the account's **API enabled** switch: `401` when off;
- the table's **API enabled** switch: `404` when off, so unpublished tables cannot be discovered.

[Access rules](/docs/access-rules) then decide which records the caller can reach.

## Demo data

`./POPULATE.sh` (source checkout) creates products, customers, orders and order lines with references between them: about 294,000 rows in roughly twenty seconds. `SCALE=0.05` reduces this to 15,000 rows.
