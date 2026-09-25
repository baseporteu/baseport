---
title: Proxy tables
description: "Tables that forward form submissions and lookups to a remote OpenAPI operation"
---

# Proxy tables

A proxy table is defined by an operation in a remote OpenAPI 3.x document. It stores no records: form submissions are forwarded to the remote operation, and lookups and lists read from its `GET` sibling.

## Creating a proxy table

**Tables > New proxy** opens the import sheet:

| Field | Purpose |
| --- | --- |
| OpenAPI 3.x spec URL | Remote document; **Fetch** lists its operations |
| Target operation | The operation that receives form submissions, usually `POST` or `PUT` |
| Path parameters | Values for placeholders in the operation path |
| Table name | Console name of the new table |
| Bearer token | Optional. Sent as `Authorization: Bearer` on every remote call, stored on this table only |

Fields are generated from the operation's request body schema:

| Schema | Field type |
| --- | --- |
| `enum` | `select` |
| `format: date` | `date` |
| `format: date-time` | `datetime` |
| `format: email` | `email` |
| `format: uri`, `url` | `url` |
| `integer`, `number` | `number` |
| `boolean` | `boolean` |
| `object` | `json` |
| `array` | `array` |
| other | `text` |

Required properties become required fields. When the body schema declares no properties (a bare `{"type":"object"}`), one record is sampled from the `GET` operation on the same path and its keys become the fields. Import fails when neither source yields a field.

## Forwarding

| Surface | Behavior |
| --- | --- |
| Form submit | Validated locally with the table's fields, then sent as JSON to the target operation. Success returns `{ "success": true, "proxy": true, "status": <remote status> }`. |
| Form lookup | `GET` on the read URL. `$filter=<field> eq '<value>'` is added when the operation declares `$filter` and the form matches on one field; otherwise `$top=1000`. The first case-insensitive match is returned. |
| Form list | `GET` on the read URL with `$top` and `$filter=contains(...)` when declared. Filtering, search, sorting and paging are applied locally to the returned records. |
| REST API | Create, update and subscribe return `400`. Nothing is stored, so reads return no rows. |

Remote failures:

| Case | Response |
| --- | --- |
| Remote returns a non-2xx status on submit | `400` with "Remote API rejected the submission (status)" and the remote error text when parseable |
| Remote unreachable on submit | `400` "The remote service could not be reached." |
| Remote returns a non-2xx status on read | `502` |
| Read timeout | `504` |
| Lookup or list on a table without a read URL | `400` "This proxy table has no readable GET endpoint." |

Every remote call is logged with method, target, outcome and elapsed time; query-string secrets are redacted.

## Outbound restrictions

All remote calls use the shared outbound guard. The host is resolved once, private and loopback addresses are refused, the connection is made to the checked address, and redirects are not followed; a target that redirects returns its `3xx`. **Allow private targets** (**Settings**) permits private addresses. Configure the final URL of the remote API.

## Editing

The target URL, read URL, method and token are editable on the table's settings after import. A blank token field keeps the stored token; the token can also be cleared. Proxy tables emit no change events and get no generated index columns.
