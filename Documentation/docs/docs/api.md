---
title: Web APIs reference
description: "Every published route, its parameters and what it answers"
---

# Web APIs reference

Everything under `/api/v1` needs a bearer token, either an account's API token or a JWT from a sign-in.

```
Authorization: Bearer <token>
```

Your own instance publishes an OpenAPI document at `/api/openapi.json`, and the console shows it at `/docs`. If you turn it off under **Settings** the document returns `404`, but the `/api/v1` routes keep working.

## Records

`{apiName}` is the name you gave the table when you published it.

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

A table publishes only the methods you left enabled on it.

### List parameters

| Parameter | Default | Notes |
| --- | --- | --- |
| `q` | none | Full text search across the record |
| `sort` | none | A field name |
| `order` | `desc` | `asc` or `desc` |
| `page` | `1` | |
| `pageSize` | `50` | Capped at 200 |
| `cursor` | none | Position from a previous response's `nextCursor` |
| `$expand` | none | Comma separated reference fields, see [Relations](/docs/relations) |

Every collection endpoint pages. None returns an unbounded list.

### Paging through everything

`page` is fine for a grid someone clicks through. For walking a whole table, follow `nextCursor` instead:

```bash
curl "http://localhost:5000/api/v1/sales-orders/records?pageSize=200" -H "Authorization: Bearer $TOKEN"
# -> { "rows": [...], "nextCursor": "eyJDIjoi..." }

curl "http://localhost:5000/api/v1/sales-orders/records?pageSize=200&cursor=eyJDIjoi..." -H "Authorization: Bearer $TOKEN"
```

`links.next` has the same URL already built. Keep going until `nextCursor` is null.

A cursor resumes from the last row of the previous page rather than counting rows to skip, so a deep page costs what the first one costs, and a record written while you page will not shift rows you have not reached yet into ones you already read. It works on the default newest-first order only. Ask for a `sort` field and a `cursor` together and you get a `400`, because the position a cursor stores does not describe that order.

### Not overwriting someone else's change

A single record response comes back with an `ETag`. Send it back as `If-Match` on a write and the write is refused with `412` if the record changed since you read it.

```bash
curl -X PATCH http://localhost:5000/api/v1/sales-orders/records/gAOPLyJDI5UU \
  -H "Authorization: Bearer $TOKEN" \
  -H 'If-Match: "gAOPLyJDI5UU-638912736000000000"' \
  -H 'Content-Type: application/json' \
  -d '{"Total":91.0}'
```

`If-Match` is optional. Without it a write still cannot silently discard another writer's change: if two writes race, the one that arrives second is refused with `409` and has to re-read. `If-Match` is how you find that out before the write instead of after.

`If-None-Match` on a read answers `304` when you already hold the current version.

### Bodies

A write body is a plain JSON object of field names, or `multipart/form-data` when you are sending a file in the same request. There is no wrapper key.

```bash
curl -X POST http://localhost:5000/api/v1/sales-orders/records \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"OrderNo":"SO-100001","Total":88.4}'
```

Server owned fields are ignored if you send them. Read only fields are too.

## Files

| Method | Route |
| --- | --- |
| `POST` | `/api/v1/files/{bucket}` |
| `GET` | `/api/v1/files/{bucket}/{name}` |
| `DELETE` | `/api/v1/files/{bucket}/{name}` |

See [Files and uploads](/docs/files).

## End user authentication

Under `/api/auth/v1`, and only when public authentication is on. See [Authentication](/docs/authentication).

| Method | Route |
| --- | --- |
| `POST` | `/api/auth/v1/register` |
| `POST` | `/api/auth/v1/login` |
| `POST` | `/api/auth/v1/anonymous` |
| `POST` | `/api/auth/v1/refresh` |
| `POST` | `/api/auth/v1/logout` |
| `GET` | `/api/auth/v1/status` |
| `POST` | `/api/auth/v1/change_password` |
| `POST` | `/api/auth/v1/delete` |
| `GET` | `/api/auth/v1/jwks.json` |

## Forms

Public, per form, and rate limited. See [Forms and embeds](/docs/forms).

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

`errors` and `invalid` are extensions on top of the standard members. `invalid` names the fields that were rejected, so you can mark them in a form without parsing the messages.

Two statuses are worth telling apart:

- `409` means the write conflicts with what is stored. A value that must be unique is already used, or another write reached the record first. Retrying with a different value may work.
- `422` means the record itself is wrong. Retrying will not help until you change it.

A table with its API access turned off answers `404`, not `403`. It reads the same as a table that does not exist, on purpose, so nobody can probe for which tables you have. A method you turned off answers `405` with an `Allow` header listing the ones that work.
