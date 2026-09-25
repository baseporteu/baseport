---
title: Tables and fields
description: "Field types, constraints and server-computed values"
---

# Tables and fields

Tables are managed under **Tables** in the console. Records are stored as JSON in one shared table, indexes are maintained automatically, and each record has a random 12-character id instead of an auto-incrementing number.

## Field types

A field's type determines the accepted values on write and the form control.

| Group | Types |
| --- | --- |
| Text | `text`, `longtext`, `richtext`, `slug`, `email`, `url`, `password` |
| Numbers | `number`, `currency` |
| Time | `date`, `datetime`, `time` |
| Choice | `boolean`, `select`, `multiselect` |
| Structured | `json`, `array`, `file`, `reference` |
| Server owned | `calculated`, `derived`, `systemid` |

`password` fields are hashed on write and excluded from every API response.

A `currency` field carries an ISO 4217 code, defaulting to the instance currency. `date` and `datetime` are stored in UTC and rendered in the instance time zone. Both defaults are set in **Settings > Host** and are included in a published form's schema, so an embed formats values the same way as the console.

## Objects and lists

A `json` field holds an object; an `array` field holds a list. Without a schema, both accept any value.

With a schema, each member is a field with the same `Required`, `Min`, `Max`, `Pattern`, select option and reference validation as a top-level field. Nesting depth is limited to three levels.

The schema is included in the OpenAPI document, so generated clients receive a typed shape. `PATCH` merges an object member by member; `PUT` replaces the whole record.

Unlike top-level fields, a key the schema does not declare is rejected rather than ignored.

Nested members cannot be `calculated`, `derived`, `systemid`, `slug` or `password`, and cannot be unique or an identifier; those are computed or checked per record at the top level only. Sorting and filtering apply to top-level fields only.

## Constraints

Every field has `Label`, `HelpText`, `DefaultValue`, `Min`, `Max` and `Pattern`, and these switches:

- **Required**: rejects a write that leaves the field empty.
- **Unique**: rejects a write that repeats a stored value.
- **Identifier**: the field a lookup form matches on. Implies **Required**.
- **Hidden**: excluded from forms.
- **Read only**: displayed in a form, not editable.

Forms, the REST API and the console share one validation path.

**Unique** and **Identifier** are checked against stored data when the field is saved. Existing duplicates are listed and the save is refused until they are resolved, which typically applies after an import.

**Identifier** requires **Required**: a record without an identifier value cannot be found by a lookup.

Comparison is case-insensitive (`A-1` equals `a-1`), matching lookup form search, so a lookup never matches two records.

`Pattern` is a regular expression evaluated with a 100 millisecond timeout, because it runs on unauthenticated requests. A pattern too slow on ordinary input is rejected when the field is saved.

## Server-computed fields

Client-supplied values for these types are ignored. Expressions are validated when the field is saved.

- `systemid`: a random short id assigned on create.
- `calculated`: a JavaScript expression over the record; the result is displayed in forms.
- `derived`: as `calculated`, never rendered in a form.

Adding one of these fields to a table with rows fills in the existing rows.

## Publishing

A table requires an **API name** before its API can be enabled: url-safe, unique, not a reserved word. Routes, OpenAPI tags and schema names derive from it, so a console rename does not change the published contract.

The methods a table answers are configurable. A disabled method is removed from the OpenAPI document and answered with `405`.
