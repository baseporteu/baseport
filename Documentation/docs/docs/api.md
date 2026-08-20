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

A table publishes only the methods you left enabled on it. A method you turned off answers `403`.

### List parameters

| Parameter | Default | Notes |
| --- | --- | --- |
| `q` | none | Full text search across the record |
| `sort` | none | A field name |
| `order` | `desc` | `asc` or `desc` |
| `page` | `1` | |
| `pageSize` | `50` | Capped at 200 |
| `$expand` | none | Comma separated reference fields, see [Relations](/docs/relations) |

Every collection endpoint pages. None returns an unbounded list.

### Bodies

A write body is a plain JSON object of field names, or `multipart/form-data` when you are sending a file in the same request. There is no wrapper key.

```bash
curl -X POST http://localhost:5263/api/v1/sales-orders/records \
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

Errors always come back in the same shape:

```json
{ "errors": ["Missing or invalid bearer token."] }
```

Validation errors also list the fields that were rejected:

```json
{ "errors": ["Field 'Total' is required."], "invalid": ["Total"] }
```

| Status | What it means |
| --- | --- |
| `400` | Something in the body or a parameter is wrong |
| `401` | Missing or invalid bearer token, or the account's API access is turned off |
| `403` | The table's API access is off, the method is not enabled, or an access rule rejected the request |
| `404` | No such table, record or route |
| `429` | You hit a rate limit. Applies to the auth and form routes |
