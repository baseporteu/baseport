---
title: SQL and scheduled queries
description: "Read-only SQL console, saved queries, and scheduled queries with webhooks"
---

# SQL and scheduled queries

Records are stored as JSON in one `_records` table. The **SQL** console runs read-only queries against the live database, for anything the REST API filters do not cover.

## Accepted statements

Statements must match:

```
^\s*(SELECT|PRAGMA|EXPLAIN|WITH|VALUES)\b
```

Other statements and multiple statements are refused; a trailing `;` is allowed. Execution adds two more guards: `query_only` on the connection, and a SQLite authorizer that allows only reading pragmas (`table_info` and similar, and settings such as `journal_mode` without an argument). A refused statement returns "That statement is not allowed here." Every statement is stopped after 10 seconds.

Results are capped at 200 rows (`SqlEngine.MaxRows`); the response reports truncation.

Field values are stored in `JsonData` and read with SQLite's JSON1 functions:

```sql
SELECT json_extract(JsonData, '$.OrderNo')  AS order_no,
       json_extract(JsonData, '$.Total')    AS total,
       CreatedAt
FROM _records
WHERE TableId = 'Kf3nQ8xR2vLm'
ORDER BY CreatedAt DESC;
```

`RecordIndexes` maintains a generated column and an index per indexable field, named `g_<fieldId>`. A plain `json_extract` query scans; the planner uses the index only when the expression matches the generated column exactly.

The console sends statements with `POST /api/_admin/fragments/sql`, so they do not appear in access logs. Row count, column count and truncation are returned in `X-Row-Count`, `X-Column-Count` and `X-Truncated`.

::: warning
The console is not filtered by [access rules](/docs/access-rules). An operator session reads every table, including `_users` and `_settings`.
:::

## Saved queries

A named query is stored as a `SavedQuery`, which can be rerun and scheduled.

## Scheduled queries

A saved query with a cron expression runs on the `JobScheduler` tick that runs maintenance jobs. Five- and six-field expressions, `@daily` and `@hourly` are accepted:

```
0 7 * * *      07:00 every day
0 0 7 * * *    the same, with an explicit seconds field
@hourly
```

With a **webhook URL**, each run POSTs its result:

```json
{
  "query": "Daily revenue",
  "ranAt": "2026-08-20T07:00:00Z",
  "columns": ["day", "total"],
  "rows": [["2026-08-19", "4210.50"]]
}
```

Without a URL, the run records its row count on the query, visible in the console.

**Run now** executes the query immediately.

Pausing keeps the cron expression: `ScheduleEnabled` is separate from `Schedule`.

### Validation

| Check | On save | At run time |
| --- | --- | --- |
| SQL is read-only and a single statement | yes | yes |
| Cron parses | yes | |
| Webhook URL resolves, and is not private or loopback | yes | yes |

At run time the outbound connection uses the `ProxyTarget` guard shared with proxy tables: the host is resolved once, private and loopback addresses are refused, the connection goes to the checked address, and redirects are not followed. **Allow private targets** (Settings) lifts the address check.

A failing run is recorded on the query and does not stop other queued jobs.
