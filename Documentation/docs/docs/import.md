---
title: Import
description: "Create a table from a file or an API, import records, and keep a table in sync with a remote source"
---

# Import

| Import | Location | Route |
| --- | --- | --- |
| New table from a file | **Tables > Import** | `POST /api/_admin/tables/import` |
| Records into an existing table | Table menu > **Import records** | `POST /api/_admin/tables/{id}/records/import` |
| New or existing table from an API | **Tables > Import from API** | `POST /api/_admin/imports` |
| Scheduled copy of an API | **Settings > Jobs > Clones** | `/api/_admin/clones` |

## File formats

| Format | Detection | Rows |
| --- | --- | --- |
| JSON | `.json` extension | An array of objects, or an object containing one |
| XML | `.xml` extension | Repeated elements, nesting up to 4 levels |
| Delimited | Any other extension | Header row plus rows; delimiter `,`, `;`, tab or `\|` |

| Limit | Value |
| --- | --- |
| File size | 8 MB |
| Rows | 5,000 per import |

A UTF-8 byte order mark is ignored.

## New table from a file

Field names are derived from the column headers (non-alphanumeric characters become `_`). The table name defaults to the file name. Field types are inferred from the first 200 rows:

| Column values | Field type |
| --- | --- |
| All `true`/`false` | `boolean` |
| All numeric | `number` |
| All parseable dates | `date` (10 characters or fewer), otherwise `datetime` |
| All e-mail addresses | `email` |
| All `http://` or `https://` | `url` |
| 2 to 12 distinct values, each used at least twice, up to 48 characters | `select` |
| Anything else | `text` |

A column with a value in every sampled row becomes **Required**, except booleans.

The sheet shows a preview first: inferred fields, the first five mapped rows and any validation errors. With **Also import the rows** selected, the rows are imported in the same request.

## Records into an existing table

Columns match fields by name, by label, or by the derived field name, case-insensitively. Server-computed fields are not matched. A file with no matching column is refused and its first headers are listed.

Every row passes the same validation as an API write, including unique values within the file itself. When any row fails, nothing is written and the response lists up to 20 failing rows with their errors.

Proxy tables cannot receive imports.

## Secrets

**Settings > Secrets** holds credentials for outbound requests. A value is encrypted at rest and never returned: the console shows only the name, when it changed and when it was last used. A secret used by a connection cannot be deleted.

Values are encrypted with the key ring in `keys/`. A backup restored without `keys/` cannot read them; enter them again.

## Connections

**Settings > Connections** describes a remote source once, for imports and clones.

| Setting | Values |
| --- | --- |
| Base URL | `http` or `https`, no credentials in the URL. Private addresses need **Allow private targets** |
| Protocol | `rest`, `odata`, or `baseport` for another Baseport instance |
| Authentication | None, bearer token, basic, or a named header; the credential is a secret |
| Headers | Up to 20, one per line. `{{name}}` sends a secret |

**Test** reads the first page. For a Baseport connection it lists the published tables instead.

## From an API

The path is relative to the base URL. For a Baseport connection the path is the table's API name, and each record's remote id is kept in `sourceId`.

The first page decides the fields, with the same type inference as a file. **Records at** names the property holding the list (`data/items`) when it is not found automatically. The import then runs in the background; the console reports the result when it finishes.

Paging is detected from the first response, or set explicitly:

| Paging | Recognized by |
| --- | --- |
| OData | `@odata.nextLink`, or `$skip` when the server omits it |
| Link header | `Link: <...>; rel="next"` |
| Next URL | `next`, `nextLink`, `next_page_url`, `links.next`, `_links.next.href`, `paging.next`, `meta.next` |
| Cursor | `nextCursor`, `next_cursor`, `nextPageToken`, `next_page_token`, `continuationToken` |
| Page number | `page` with `totalPages`, `total_pages` or `last_page` |
| Offset | `offset` or `skip` with `total`, `count` or `totalCount` |

| Limit | Value |
| --- | --- |
| Rows | 100,000 per run |
| Pages | 2,000 per run |
| Page size | 10 MB |
| Response time | 30 seconds per page |

A next page on another origin stops the run before any credential is sent there. A page seen before ends the run.

## Clones

A clone repeats an import on a schedule, into an existing table.

| Mode | Effect |
| --- | --- |
| Upsert | Adds new records and updates changed ones, matched on the key field |
| Mirror | Upsert, then deletes records the source no longer has |
| Append | Adds every record, every run |

A mirror applies only a complete fetch, in one transaction. It refuses to delete more than half the table unless **Allow a mirror to delete more than half the table** is on. Rows that fail validation are skipped and listed on the run. One run per clone at a time; **Run now** queues one immediately.
