---
title: Relations
description: "Reference fields, relationship shapes and expansion"
---

# Relations

A `reference` field stores the id of one record in a target table, chosen when the field is created. The value is the target record's id as a string.

A write sends the id:

```json
{ "OrderNo": "SO-100000", "Customer": "T7mQ2xR9vLbK" }
```

The id is validated on write; an id with no matching record in the target table returns `422`.

## Relationship shapes

A reference holds one id; the relationship is determined by which table holds the field.

| Shape | Model |
| --- | --- |
| One to many | Reference on the "many" side: a `Customer` field on **Orders**. |
| One to one | As one to many, with **Unique** on the reference field. |
| Many to many | A junction table with two references. |

Many to many, orders and tags:

| Table | Fields |
| --- | --- |
| Orders | `OrderNo`, and the rest of the order |
| Tags | `Name` |
| OrderTags | `Order` (reference to Orders), `Tag` (reference to Tags) |

Each OrderTags row pairs one order with one tag. An order's tags: list OrderTags filtered by `Order`, with `expand=Tag`.

## Links

Every record in an API response has a `links` block with `self`, `collection` and one entry per reference field:

```json
{
  "id": "gAOPLyJDI5UU",
  "data": { "OrderNo": "SO-100000", "Customer": "T7mQ2xR9vLbK" },
  "links": {
    "Customer": "/api/v1/customers/records/T7mQ2xR9vLbK",
    "self": "/api/v1/sales-orders/records/gAOPLyJDI5UU",
    "collection": "/api/v1/sales-orders/records"
  }
}
```

A reference is included only when the target table is published and allows `GET`.

## Expanding a reference

`expand` includes the referenced record in the same response:

```bash
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5000/api/v1/sales-orders/records?expand=Customer"
```

```json
{
  "id": "gAOPLyJDI5UU",
  "data": { "OrderNo": "SO-100000", "Customer": "T7mQ2xR9vLbK" },
  "expanded": {
    "Customer": { "id": "T7mQ2xR9vLbK", "data": { "Name": "Ada Byrne" } }
  }
}
```

Multiple fields are comma-separated. A name that is not an expandable reference field returns `400`.

Expansion is one level deep, on list and single-record reads alike.

## Access rules on the target

Expansion reads the target through its own read rule. A target the caller cannot read is omitted from `expanded`; the parent record is still returned. The `links` entry remains, since it is built from the stored id.

## Deleting a referenced record

References are validated on write only. Deleting a record does not change records that refer to it; they keep the old id.

Reads of those records succeed, with the target omitted from `expanded`. **Updates fail**, even when the reference is not changed, because an update revalidates the whole record:

```json
{
  "status": 422,
  "detail": "Customer references a record that doesn't exist.",
  "invalid": ["Customer"]
}
```

Delete or repoint referring records before deleting their target. A [scheduled query](/docs/sql-and-queries) can report orphans.
