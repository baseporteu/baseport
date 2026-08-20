---
title: Access rules
description: "Per record create, read, update and delete rules, evaluated by SQLite"
---

# Access rules

Once you publish a table, every caller who gets past the two API switches can see all of it. Access rules narrow that down to individual records.

Each table carries four rules, and each one is a SQLite boolean expression:

```sql
_ROW_.owner = _USER_.id
```

Set them on the table's access panel: **create**, **read**, **update** and **delete**. An empty rule means no rule.

## What a rule can refer to

| Reference | Resolves to |
| --- | --- |
| `_USER_.id` | The id of the calling account. Only `id` is available. |
| `_ROW_.<field>` | A field on the stored record. `NULL` on create, because there is no row yet. |
| `_REQ_.<field>` | A field in the incoming request body. |

There is no custom expression language here. Baseport turns your references into `json_extract` calls with bound parameters and passes the whole thing to SQLite, so anything you can write in a SQLite `WHERE` clause works.

```sql
_ROW_.status = 'open' AND _ROW_.owner = _USER_.id
```

```sql
_REQ_.total < 1000
```

## What happens when a rule fails

A read rule **filters a list** instead of rejecting the request, so a caller gets their own records back rather than a `403`. Everywhere else, including reading one record by id, a rule that does not hold means the request is rejected.

A rule that evaluates to `NULL`, or that refers to a record that no longer exists, counts as a rejection.

Live updates are filtered the same way reads are. If a caller could not fetch a record, they do not get an event for it either. The rule is re-read for every event, so tightening a rule affects connections that are already open.

## Rules are checked when you save

Rules are checked when you save the table, not when somebody calls it. An unknown field name, a stray `;`, an alias other than the three above, and anything SQLite itself rejects will all stop the save. Otherwise a bad rule would turn into a `500` on every request to that table.

## Where rules do not apply

The console at `/api/_admin/*` is never filtered. If you are signed in as an operator you see everything.

The Postgres and TDS listeners are filtered like the API, not like the console. They authenticate with an API token, so they only see published tables, each one behind its read rule. A SQLite authorizer also blocks any direct read of the underlying schema, so `_users` and `_settings` are not reachable from them.
