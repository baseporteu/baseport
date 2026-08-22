---
title: Tables and fields
description: "Modelling your data: field types, constraints and server-computed values"
---

# Tables and fields

Tables live under **Tables** in the console. Records are stored as JSON in one shared table, indexes are managed for you, and every record gets a random 12-character id instead of an auto-incrementing number.

## Field types

A field's type decides what is accepted on write and what the form draws.

| Group | Types |
| --- | --- |
| Text | `text`, `longtext`, `richtext`, `slug`, `email`, `url`, `password` |
| Numbers | `number`, `currency` |
| Time | `date`, `datetime`, `time` |
| Choice | `boolean`, `select`, `multiselect` |
| Structured | `json`, `array`, `file`, `reference` |
| Server owned | `calculated`, `derived`, `systemid` |

`password` fields are hashed on write and never come back in an API response.

A `currency` field has its own ISO 4217 code, or falls back to the instance default. `date` and `datetime` are stored in UTC and rendered in the instance time zone. Both defaults are in **Settings > Host**, and both travel with a published form's schema so an embed on somebody else's page formats the same way the console does.

## Objects and lists

A `json` field holds an object and an `array` field holds a list. Leave them without a schema and they take whatever you send.

Give one a schema and each member becomes a real field, with `Required`, `Min`, `Max`, `Pattern`, select options and references all working the same way they do at the top level. You can nest three levels deep.

The schema shows up in the OpenAPI document too, so generated clients get the shape rather than an untyped object. `PATCH` merges an object member by member, so changing one member does not drop the rest. `PUT` still replaces the whole record.

One difference from top-level fields: a key the schema does not declare is rejected rather than ignored. The schema and the object are written together, so an unexpected member is a mistake worth hearing about.

Nested members cannot be `calculated`, `derived`, `systemid`, `slug` or `password`, and cannot be unique or an identifier. All of those are computed or checked over a whole record, and none of those write paths run below the top level. Sorting and filtering only reach top-level fields as well.

## Constraints

Every field has `Label`, `HelpText`, `DefaultValue`, `Min`, `Max` and `Pattern`, plus these switches:

- **Required** rejects a write that leaves the field empty
- **Unique** rejects a write that repeats a value already stored
- **Identifier** is the field a lookup form matches on. Turning it on turns **Required** on too
- **Hidden** keeps the field out of forms
- **Read only** shows the value in a form but does not let anyone edit it

All of this is enforced in one place. Writes from a form, from the REST API and from the console run through the same validation code, so they cannot disagree.

Turning **Unique** or **Identifier** on is a claim about the rows you already have, not just about the next write, so both are checked against the stored data when you save the field. If the column has duplicates you are told which values, and the save is refused until you clear them. That usually comes up right after an import, where the column you want as a key is the one the file already had.

**Identifier** has to be **Required** because it is the value someone types to find their own record. A row without one could never be found by it.

Values are compared without regard to case, so `A-1` and `a-1` count as the same value. That matches how a lookup form searches, which is what stops a form finding two records where you meant one.

`Pattern` is a regular expression. It runs on requests that need no authentication, so it is evaluated with a 100 millisecond timeout, and a pattern too slow on ordinary input is rejected when you save the field instead of failing later.

## Server-computed fields

These three ignore anything a client sends for them. The expression is checked when you save the field, not when somebody writes a record.

- `systemid` gets a random short id when the record is created
- `calculated` runs a JavaScript expression over the record and shows the result in forms
- `derived` is the same thing, but never rendered in a form

Add one of these to a table that already has rows and the existing rows are filled in too, so you do not end up with a column that is populated only for records somebody happened to edit afterwards.

## Publishing

A table needs an **API name** before you can turn its API on: url-safe, unique, and not a reserved word. Routes, OpenAPI tags and schema names all come from that name, so renaming the table in the console cannot break anything already calling it.

You can also restrict which methods a table answers. Turn `DELETE` off and it disappears from the OpenAPI document as well as from the routes, rather than being documented as something that then refuses.
