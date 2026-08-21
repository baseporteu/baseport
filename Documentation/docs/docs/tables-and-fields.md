---
title: Tables and fields
description: "Modelling your data: field types, constraints and server-computed values"
---

# Tables and fields

You create a table under **Tables** and add fields to it. Records are stored as JSON in one shared store, each with a random twelve character id. There are no auto-increment integer keys, so a record has the same id whether you reach it through a relation, the SQL console or the API.

Indexes are created and maintained for you.

## Field types

Every field has a type. The type decides how the value is validated and how it appears in a form.

| Group | Types |
| --- | --- |
| Text | `text`, `longtext`, `richtext`, `slug`, `email`, `phone`, `url`, `password` |
| Numbers | `number`, `currency`, `rating` |
| Time | `date`, `datetime`, `time` |
| Choice | `boolean`, `select`, `multiselect`, `color` |
| Structured | `json`, `array`, `file`, `reference` |
| Server owned | `calculated`, `derived`, `systemid` |

A `password` field is hashed when written and never included in an API response.

A `currency` field picks its own ISO 4217 code, or leaves it empty to follow the instance default. `date` and `datetime` values are stored in UTC and rendered in the instance time zone. Both defaults live in **Settings > Host**, and both travel with the published form schema, so a form renders the same amounts and the same clock wherever it is embedded.

## Constraints

Every field has `Label`, `HelpText`, `DefaultValue`, `Min`, `Max` and `Pattern`, plus these switches:

- **Required** rejects a write that leaves the field empty
- **Unique** rejects a write that repeats a value already stored
- **Identifier** is the field a lookup form matches on
- **Hidden** keeps the field out of forms
- **Read only** shows the value in a form but does not let anyone edit it

All of this is enforced in one place. Writes from a form, from the REST API and from the console all run through the same validation code, so they cannot disagree.

`Pattern` is a regular expression. Because it runs on requests that need no authentication, it is evaluated with a 100 millisecond timeout, and a pattern too slow on ordinary input is rejected when you save the field rather than failing later.

## Server-computed fields

Three field types are computed on the server. If a client sends a value for one, it is ignored.

- `systemid` gets a new random short id when the record is created.
- `calculated` runs a JavaScript expression against the record and stores the result. It shows up in forms.
- `derived` works the same way but never appears in a form. Use it for a value you want stored but not shown.

Expressions are checked when you save the field, so you find out about a mistake there instead of on every write afterwards.

## Publishing

A table has an `ApiName` as well as a `Name`. It has to be url safe and unique, and you need one before you can turn **API enabled** on. Routes, OpenAPI tags and schema names all come from it, so renaming the table in the console does not change anything a client is calling.

You can also limit which methods the table accepts, from the full `GET,POST,PATCH,PUT,DELETE` down to whichever ones make sense.
