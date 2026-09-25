---
title: Access rules
description: "Per record create, read, update and delete rules, evaluated by SQLite"
---

# Access rules

A published table without rules exposes every row to any caller that passes both API switches. Access rules restrict records per caller.

Each table has four rules, set on the table's access panel: **create**, **read**, **update** and **delete**. Each rule is a SQLite boolean expression. An empty rule means no restriction.

```sql
_ROW_.owner = _USER_.id
```

## References

| Reference | Resolves to |
| --- | --- |
| `_USER_.id` | Id of the calling account. |
| `_USER_.role` | Role of the calling account: `admin`, `consumer` or `user`. |
| `_ROW_.<field>` | A field on the stored record. `NULL` on create. |
| `_REQ_.<field>` | A field in the request body. |

References are rewritten into `json_extract` calls and bound parameters; the rest of the expression is passed to SQLite unchanged. Any expression valid in a SQLite `WHERE` clause is accepted.

```sql
_ROW_.status = 'open' AND _ROW_.owner = _USER_.id
```

```sql
_REQ_.total < 1000
```

```sql
_USER_.role = 'consumer'
```

The last example refuses end-user JWTs and allows service tokens.

## Evaluation

| Operation | Rule result `false` or `NULL` |
| --- | --- |
| List (`GET /records`) | Row omitted from the result |
| Get, create, update, delete | `403` |
| Live updates (SSE) | Event not delivered |

A rule that refers to a deleted record evaluates as a refusal. Live updates re-read the rule for every event; a changed rule applies to open streams. The role reaches every evaluation: single-record checks, list filters, relation expansion, transactions, SSE and the wire views.

## Validation

Rules are validated when the table is saved. The save is refused for:

- an unknown field name or alias (only `_USER_`, `_ROW_` and `_REQ_`);
- `;`, comments (`--`, `/*`), `{` or `}`;
- parameter markers (`?`, `$`, `:`, `@`) outside a quoted string;
- an unclosed quote or bracket, or unbalanced parentheses;
- `_USER_`, `_ROW_` or `_REQ_` inside a quoted string;
- anything SQLite rejects.

## Scope

- The console (`/api/_admin/*`) is never filtered.
- The Postgres and TDS listeners apply read rules like the REST API. They authenticate with an API token and expose published tables only, each as a view filtered by its read rule. A SQLite authorizer blocks direct reads of the underlying tables, including `_users` and `_settings`.
