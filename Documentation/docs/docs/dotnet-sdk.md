---
title: .NET SDK
description: "Reading and writing records from a .NET app without hand-rolling HTTP"
---

# .NET SDK

`Baseport.Client` targets `net8.0`, so MAUI, WPF, Avalonia and ASP.NET Core hosts can all take it. It wraps the same `/api/v1` routes you would otherwise call with `HttpClient`.

```bash
dotnet add package Baseport.Client
```

## Register it

```csharp
services.AddBaseport(o =>
{
    o.BaseUrl = "http://localhost:5000";
    o.ApiToken = builder.Configuration["Baseport:ApiToken"];
});
```

Registers `IBaseportClient` as a singleton over `IHttpClientFactory`. For end-user sessions rather than a shared static token, leave `ApiToken` unset and call `LoginAsync`; the client refreshes a minute before expiry.

## Records

`Records()` takes the table's published API name, not its internal one.

```csharp
public sealed record SalesOrder(string OrderNo, decimal Total);

var orders = client.Records("sales-orders");

var id = await orders.CreateAsync(new { OrderNo = "SO-100001", Total = 88.4m });

var page = await orders.ListAsync(query: "SO-1000", pageSize: 25);
foreach (var row in page.Rows)
    Console.WriteLine(row.As<SalesOrder>()?.OrderNo);

await orders.UpdateAsync(id, new { Total = 91.0m });
await orders.DeleteAsync(id);
```

`As<T>()` deserializes the record's `data` into your own type.

## Walking a whole table

```csharp
await foreach (var row in orders.WalkAsync(pageSize: 200, cancellationToken: ct))
    Process(row.As<SalesOrder>());
```

Keyset paging under the hood, so page 500 costs what page 1 costs and an insert mid-walk cannot shift unread rows into what you already read. Prefer it over looping `page: 1, 2, 3`, which is `OFFSET` and degrades linearly.

## Optimistic concurrency

Records come back with an `ETag`. Pass it to a write and the write is refused if the record moved on since you read it:

```csharp
var order = await orders.ReadAsync(id);

try
{
    await orders.UpdateAsync(id, new { Total = 91.0m }, order.ETag);
}
catch (BaseportException e) when (e.IsPreconditionFailure)
{
    // Someone wrote first. Re-read and decide what to do with their version.
}
```

The `ETag` is optional. A write that loses a race is refused with `e.IsConflict` either way, because the server holds a concurrency token on the record. Sending the `ETag` moves the failure earlier and tells you which version you were working from.

## Errors

Failures throw `BaseportException`, which exposes the server's problem document through `Detail`, `InvalidFields` and a few predicates:

```csharp
catch (BaseportException e) when (e.IsValidationFailure)
{
    // e.InvalidFields names the fields that were rejected
}
catch (BaseportException e) when (e.IsConflict)
{
    // A value that must be unique is already used, or a write lost a race
}
```

Split the two in your retry logic: a conflict may succeed with a different value, a validation failure never will.

## Live updates

```csharp
await foreach (var change in orders.SubscribeAsync(ct))
    Console.WriteLine($"{change.Action} {change.Id}");
```

Pass a record id to watch one row instead of the table. Access rules apply to a stream the same way they apply to a read.

## Files

```csharp
var stored = await client.Storage
    .Bucket("avatars")
    .UploadAsync("photo.png", "image/png", stream, ct);
```

`stored.Url` is the address to hand back to a browser. See [Files and uploads](/docs/files).

## If you only need to verify tokens

A service that just checks Baseport JWTs does not need this package. Point `AddJwtBearer` at `/api/auth/v1/jwks.json` with the issuer and audience from **Settings › Authentication**.
