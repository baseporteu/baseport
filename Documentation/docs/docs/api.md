---
title: Web APIs reference
description: "Published routes, parameters and responses"
---

# Web APIs reference

Every route under `/api/v1` requires a bearer token: an account's API token or a JWT from a sign-in.

```
Authorization: Bearer <token>
```

Each instance publishes an OpenAPI 3.2 document at `/api/openapi.json`, rendered at `/docs`. Turning it off in **Settings** makes the document return `404`; the `/api/v1` routes are unaffected.

## Records

`{apiName}` is the table's API name.

| Method | Route | Notes |
| --- | --- | --- |
| `GET` | `/api/v1/{apiName}/records` | Paged, searchable, sortable |
| `GET` | `/api/v1/{apiName}/records/{id}` | One record |
| `POST` | `/api/v1/{apiName}/records` | Create |
| `PATCH` | `/api/v1/{apiName}/records/{id}` | Merge onto the stored record |
| `PUT` | `/api/v1/{apiName}/records/{id}` | Replace it |
| `DELETE` | `/api/v1/{apiName}/records/{id}` | Delete |
| `GET` | `/api/v1/{apiName}/subscribe` | Server-Sent Events for the table |
| `GET` | `/api/v1/{apiName}/subscribe/{id}` | Server-Sent Events for one record |

A table answers only its enabled methods.

### List parameters

| Parameter | Default | Notes |
| --- | --- | --- |
| `q` | none | Full text search across the record |
| `sort` | none | A field name |
| `order` | `desc` | `asc` or `desc` |
| `page` | `1` | |
| `pageSize` | `50` | Capped at 200 |
| `cursor` | none | Position from a previous response's `nextCursor` |
| `expand` | none | Comma separated reference fields, see [Relations](/docs/relations) |

Every collection endpoint is paged.

### Cursor paging

`page` suits interactive grids. Full-table walks use `nextCursor`:

```bash
curl "http://localhost:5000/api/v1/sales-orders/records?pageSize=200" -H "Authorization: Bearer $TOKEN"
# -> { "rows": [...], "nextCursor": "eyJDIjoi..." }

curl "http://localhost:5000/api/v1/sales-orders/records?pageSize=200&cursor=eyJDIjoi..." -H "Authorization: Bearer $TOKEN"
```

`links.next` contains the same URL. The walk ends when `nextCursor` is null.

The cursor is a keyset on `(CreatedAt, Id)` of the previous page's last row: every page has the same cost, and concurrent inserts do not shift rows between pages.

Cursors apply to the default `CreatedAt DESC, Id DESC` order only; `sort` with `cursor` returns `400`.

### Optimistic concurrency

Single-record responses carry an `ETag`. A write with `If-Match` returns `412` when the record changed since it was read.

```bash
curl -X PATCH http://localhost:5000/api/v1/sales-orders/records/gAOPLyJDI5UU \
  -H "Authorization: Bearer $TOKEN" \
  -H 'If-Match: "gAOPLyJDI5UU-638912736000000000"' \
  -H 'Content-Type: application/json' \
  -d '{"Total":91.0}'
```

`If-Match` is optional. `Record.UpdatedAt` is a concurrency token in the `UPDATE`'s `WHERE` clause, so a write that loses a race returns `409` without a precondition. `If-Match` reports the conflict before the write is attempted.

`If-None-Match` on a read returns `304` for the current version.

### Bodies

A write body is a JSON object keyed by field name, or `multipart/form-data` when it includes a file. There is no wrapper key.

```bash
curl -X POST http://localhost:5000/api/v1/sales-orders/records \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"OrderNo":"SO-100001","Total":88.4}'
```

Values for server-owned and read-only fields are ignored.

## Transactions

`POST /api/transaction/v1/execute` runs up to 128 operations across tables in one call.

```json
{
  "transaction": true,
  "operations": [
    { "op": "create", "apiName": "sales-orders", "value": { "OrderNo": "SO-100002" } },
    { "op": "update", "apiName": "customers", "recordId": "T7mQ2xR9vLbK", "value": { "Name": "Ada" } },
    { "op": "delete", "apiName": "order-tags", "recordId": "b8Xk2mQ9pLwR" }
  ]
}
```

| `transaction` | Behavior |
| --- | --- |
| `false` (default) | Each operation commits on its own; an error leaves earlier operations applied. |
| `true` | One database transaction; the first error rolls back the batch. |

Each operation passes the same validation, method switches and access rules as the single-record routes.

## Files

| Method | Route |
| --- | --- |
| `POST` | `/api/v1/files/{bucket}` |
| `GET` | `/api/v1/files/{bucket}/{name}` |
| `DELETE` | `/api/v1/files/{bucket}/{name}` |

See [Files and uploads](/docs/files).

## End user authentication

Available when public authentication is on. See [Authentication](/docs/authentication).

| Method | Route |
| --- | --- |
| `POST` | `/api/auth/v1/register` |
| `POST` | `/api/auth/v1/login` |
| `POST` | `/api/auth/v1/anonymous` |
| `POST` | `/api/auth/v1/refresh` |
| `POST` | `/api/auth/v1/logout` |
| `GET` | `/api/auth/v1/status` |
| `POST` | `/api/auth/v1/change_password` |
| `DELETE` | `/api/auth/v1/delete` |
| `GET` | `/api/auth/v1/jwks.json` |

## Forms

Anonymous, per form, rate limited. See [Forms and embeds](/docs/forms).

| Method | Route |
| --- | --- |
| `GET` | `/api/forms/{formId}/schema` |
| `GET` | `/api/forms/{formId}/list` |
| `GET` | `/api/forms/{formId}/form` |
| `POST` | `/api/forms/{formId}/form` |
| `GET` | `/api/forms/{formId}/reference/{fieldName}` |
| `GET` | `/f/{formId}` |

## Errors

Errors are [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem documents, served as `application/problem+json`:

```json
{
  "type": "urn:baseport:problem:validation-failed",
  "title": "Validation failed",
  "status": 422,
  "detail": "Field 'Total' is required.",
  "instance": "/api/v1/sales-orders/records",
  "errors": ["Field 'Total' is required."],
  "invalid": ["Total"]
}
```

`errors` and `invalid` extend the standard members. `invalid` lists the rejected field names.

| Status | Meaning |
| --- | --- |
| `409` | Conflict with stored data: a duplicate unique value, or a concurrent write. |
| `422` | The record is invalid; a retry needs a changed body. |
| `503` | Subscriber limit reached on `/subscribe` (1,000 per instance). `Retry-After: 30`. |

A table with its API off returns `404`, identical to a missing table. A disabled method returns `405` with an `Allow` header.
