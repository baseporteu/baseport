---
title: SQL and scheduled queries
description: "The read-only SQL console, saved queries, and running one on a cron with a webhook"
---

# SQL and scheduled queries

Records are stored as JSON in one `_records` table, so anything you want that the REST API does not expose as a filter is a SQL query. The **SQL** rail item is a console over the live database.

## What the console will run

Statements are checked before they execute:

```
^\s*(SELECT|PRAGMA|EXPLAIN|WITH|VALUES)\b
```

Anything else is refused, and so is more than one statement. A trailing `;` is fine, one in the middle is not. The connection is opened read-only and `query_only` is set on top of that, so the check is belt and braces rather than the only thing standing between you and a `DROP`.

Results cap at 200 rows (`SqlEngine.MaxRows`). The response says whether it hit the cap instead of quietly handing you a short answer.

Field values live in `JsonData`, so most queries go through SQLite's JSON1 functions:

```sql
SELECT json_extract(JsonData, '$.OrderNo')  AS order_no,
       json_extract(JsonData, '$.Total')    AS total,
       CreatedAt
FROM _records
WHERE TableId = 'Kf3nQ8xR2vLm'
ORDER BY CreatedAt DESC;
```

`RecordIndexes` maintains a generated column plus an index per indexable field, named `g_<fieldId>`. Querying `json_extract` directly still works and still scans; the planner uses the index when you match the generated column's expression exactly.

The console posts rather than gets (`POST /api/_admin/fragments/sql`), so your statement does not end up in an access log. Row count, column count and whether the result was truncated come back in `X-Row-Count`, `X-Column-Count` and `X-Truncated`.

::: warning
The console is not filtered by [access rules](/docs/access-rules). An operator session reads everything, including `_users` and `_settings`. That is deliberate, and it is why console access is the thing to guard.
:::

## Saved queries

Name a query and it is stored as a `SavedQuery`. That gets you a query you can rerun, and the row a schedule attaches to.

## Scheduling one

Give a saved query a cron expression and the same `JobScheduler` tick that runs the maintenance jobs runs it too. Five and six field expressions both parse, along with `@daily` and `@hourly`:

```
0 7 * * *      07:00 every day
0 0 7 * * *    the same, with an explicit seconds field
@hourly
```

Set a **webhook URL** and each run POSTs its grid there:

```json
{
  "query": "Daily revenue",
  "ranAt": "2026-08-20T07:00:00Z",
  "columns": ["day", "total"],
  "rows": [["2026-08-19", "4210.50"]]
}
```

Leave the URL empty and the run records its row count against the query instead, which you read in the console.

**Run now** fires it immediately, so you can check the destination works without waiting for the schedule.

Pausing a schedule keeps the cron expression (`ScheduleEnabled` is a separate flag from `Schedule`), so you are not retyping it to switch a report off for a week.

### What is validated, and when

| Check | On save | At run time |
| --- | --- | --- |
| SQL is read-only and a single statement | yes | yes |
| Cron parses | yes | |
| Webhook URL resolves, and is not private or loopback | yes | yes |

The cron and the URL are checked when you save for the same reason an access rule is: a typo should be a message in the sheet, not a report that silently never arrives.

The URL is checked again at run time because DNS moves. A hostname that resolved to a public address when you saved it can resolve to `169.254.169.254` later, and this is the same `ProxyTarget` guard proxy tables use. Private and loopback destinations are refused unless **Allow private proxy targets** is on in Settings.

A failing run is recorded on the query rather than thrown, so one broken report does not stop the jobs queued behind it.
