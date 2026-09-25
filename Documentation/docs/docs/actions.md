---
title: Actions
description: "Record-triggered steps: expressions, record updates and outbound HTTP requests"
---

# Actions

An action runs a list of steps when a record in its table is created, updated or deleted. Actions are managed under **Actions** in the console.

## Definition

| Setting | Values |
| --- | --- |
| Name | Required, up to 128 characters |
| Table | Fixed after creation |
| Trigger | `onCreate` (When a record is created), `onUpdate`, `onDelete` |
| Enabled | A disabled action enqueues nothing; queued runs complete without executing |
| Steps | At least one |

Steps and expressions are validated on save.

## Steps

| Type | Settings | Effect |
| --- | --- | --- |
| `runExpression` | `expr` | Evaluates a JavaScript expression over the record |
| `updateRecord` | `setJson`: field name to expression | Merges the results into the record through the normal write path, including validation. Server-computed fields cannot be set. The update does not trigger actions again. |
| `httpRequest` | `url`, `method`, `headers`, `bodyTemplate` | Sends a request. `bodyTemplate` maps JSON keys to expressions over the record; the body is sent as JSON for every method except `GET` |

Expressions reference record fields by name. `httpRequest` methods: `GET`, `POST`, `PUT`, `PATCH`, `DELETE`. The request times out after 30 seconds; a non-2xx response fails the step.

`httpRequest` uses the outbound guard shared with proxy tables: private and loopback addresses are refused unless **Allow private targets** is on, the host is resolved once, and redirects are not followed.

## Runs

Each matching record event enqueues one run per enabled action. The scheduler processes due runs every 30 seconds, up to 50 per tick.

| Outcome | Result |
| --- | --- |
| All steps succeed | `Done` |
| A step fails | Retried after 1, 2, 4 and 8 minutes; `Failed` after 5 attempts, with the last error |
| Record or table deleted before the run | `Done`, no steps executed |

Steps run in order; a failing step stops the run, and a retry starts again from the first step. Steps that already ran before a failure are not rolled back.

The action sheet lists the 50 most recent runs with status, attempts, next attempt and last error.

:::warning
`onDelete` runs start after the record is deleted, so they complete without executing their steps.
:::
