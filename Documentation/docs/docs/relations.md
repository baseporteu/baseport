---
title: Relations
description: "Reference fields, modelling one to many and many to many, and expanding a related record"
---

# Relations

A `reference` field holds the id of one record in another table. You pick the target table when you add the field, and the field stores that record's id as a string. That is the whole mechanism: one field, one id, one target record.

Writing one means sending the id:

```json
{ "OrderNo": "SO-100000", "Customer": "T7mQ2xR9vLbK" }
```

The id is checked on write. If no record with that id exists in the target table you get a `400` saying so, rather than a row pointing at nothing.

## Modelling the usual shapes

Because a reference field holds exactly one id, the relationship you get depends on where you put the field.

**One to many.** A customer has many orders and each order has one customer. Put the reference on the many side: give **Orders** a `Customer` reference field pointing at Customers. This is the common case and the one you will use most.

**One to one.** A customer has one billing profile. Same as above, but also tick **Unique** on the reference field, so no two records can point at the same target.

**Many to many.** An order carries several tags and a tag appears on several orders. One field cannot hold two ids, so add a third table whose rows are the pairings:

| Table | Fields |
| --- | --- |
| Orders | `OrderNo`, and the rest of the order |
| Tags | `Name` |
| OrderTags | `Order` (reference to Orders), `Tag` (reference to Tags) |

One row in OrderTags means one tag on one order. To list an order's tags, read OrderTags filtered by that order, expanding `Tag`.

## Links

Every record in an API response carries a `links` block. Reference fields appear in it beside `self` and `collection`:

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

A reference only appears in `links` if the target table is published and allows `GET`. There is no point giving you a link you cannot follow.

## Expanding a reference

Follow the reference in the same request with `$expand`, so you do not need a second call:

```bash
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5263/api/v1/sales-orders/records?\$expand=Customer"
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

Separate several field names with commas. If you name something that is not an expandable reference field you get a `400`, so a typo is reported rather than silently ignored.

Expansion goes one level deep. To follow a chain, expand at each step or read the next record directly. This works the same way when you read a single record.

## Access rules apply to the target

Expanding a reference re-reads the target table through its own read rule. If the caller could not have fetched that customer directly, the customer is left out of `expanded`, even though the order is still returned. The `links` entry stays, since it is built from an id already stored on the order.

## Deleting a record something points at

Baseport checks a reference when you write it, not afterwards, and deleting a record does not touch anything referring to it. So if you delete a customer, the orders that pointed at them keep the old id.

Two things follow. Reading those orders still works, and `expanded` simply leaves the customer out. But **editing one of them fails**, even if you are not touching the reference: an update revalidates the whole record, and the reference no longer resolves.

```json
{ "errors": ["Customer references a record that doesn't exist."], "invalid": ["Customer"] }
```

If you delete records that others point at, delete or repoint the children first. A [scheduled query](/docs/going-to-production) is a reasonable way to find orphans before they become a surprise.
