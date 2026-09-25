---
title: Import
description: "Create a table from a file, or import records into an existing table"
---

# Import

Two console imports read CSV, JSON or XML files:

| Import | Location | Route |
| --- | --- | --- |
| New table from a file | **Tables > Import** | `POST /api/_admin/tables/import` |
| Records into an existing table | Table menu > **Import records** | `POST /api/_admin/tables/{id}/records/import` |

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
